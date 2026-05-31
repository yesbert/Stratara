---
title: "Append-only doesn't mean what you'd hope"
datePublished: 2026-05-31T15:31:25.859Z
cuid: cmptxtdsv00041spt41qpawip
slug: append-only-doesn-t-mean-what-you-d-hope
tags: security, event-sourcing, dotnet, csharhp

---

Event sourcing gets sold on immutability. You don't update, you don't delete, you only append, so the history is permanent.

It mostly isn't. The events are immutable because your code agrees not to touch them, not because anything actually stops it. Underneath they're still rows in Postgres, and rows have a DBA with write access. A migration that "cleans up" old data. A 2 a.m. query run against the wrong connection. A backup restored with slightly different bytes in it.

Change one of those rows and a replay won't blink. The aggregate rebuilds, the projections rebuild, everything looks fine. Usually the first person to notice is a customer whose balance is off, and by then the trail is cold.

## Chain each event into the next

The trick is small. Give every row two extra columns: a hash of its contents, and the hash of the row before it.

![Image description](https://dev-to-uploads.s3.amazonaws.com/uploads/articles/rx8mi8pgtp1bcd3lctdo.png align="center")

The hash is `SHA-256(previousHash || json(payload))`. Nothing exotic.

The point is that each hash depends on the one before it. Edit a payload and its hash stops matching. Rewrite that hash to cover for the edit, and now the next row's pointer is wrong. You can't fix one without breaking the next.

## About forty lines of it

Appending an event hashes it together with the previous one:

```csharp
public HashChainedEntry Append(object payload)
{
    var previousHash = _entries.Count == 0 ? GenesisHash : _entries[^1].Hash;
    var hash = ComputeHash(previousHash, payload);
    var entry = new HashChainedEntry(_entries.Count + 1, payload, previousHash, hash);
    _entries.Add(entry);
    return entry;
}

internal static byte[] ComputeHash(byte[] previousHash, object payload)
{
    var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
    var combined = new byte[previousHash.Length + payloadJson.Length];
    Buffer.BlockCopy(previousHash, 0, combined, 0, previousHash.Length);
    Buffer.BlockCopy(payloadJson, 0, combined, previousHash.Length, payloadJson.Length);
    return SHA256.HashData(combined);
}
```

Verifying is the same thing backwards. Walk the rows, recompute, and check two things on each one: the pointer and the hash.

```csharp
byte[] previousHash = new byte[32]; // genesis
foreach (var entry in store.Entries)
{
    if (!ByteArraysEqual(previousHash, entry.PreviousHash))
        throw new EventStreamCorruptedException(entry.Sequence,
            "previous-hash pointer does not match the prior entry's hash");

    var recomputed = ComputeHash(previousHash, entry.Payload);
    if (!ByteArraysEqual(recomputed, entry.Hash))
        throw new EventStreamCorruptedException(entry.Sequence,
            "stored hash does not match a fresh re-hash of the payload (payload was modified after commit)");

    previousHash = entry.Hash;
}
```

Bump Alice's $50 deposit to $5,000 straight in the table, and the check stops you cold at the exact row:

```plaintext
Event stream tampering detected at sequence #2: stored hash does not
match a fresh re-hash of the payload (payload was modified after commit)
```

## What that gets you

| Someone tries to… | …and it shows up because |
| --- | --- |
| Edit one event's payload | the re-hash no longer matches the stored hash |
| Rewrite the stored hash to match | the next row's pointer no longer matches |
| Delete a row from the middle | the next row's pointer doesn't match its new neighbour |
| Slip in a forged row | same thing, the pointer chain breaks at the seam |

## The honest ceiling

Here's the part people gloss over. That table assumes the attacker is lazy: edit a row, move on, leave the stale hash behind. Someone with full write access doesn't have to be lazy. They can edit the row and then recompute every hash after it. Now the chain is consistent again and the verifier has nothing to say.

A hash chain is a checksum, not a signature. If you own both ends of it, so does anyone who owns your database. That's the honest ceiling of doing this inside your own four walls, and it's worth saying out loud before someone says it for you in the comments.

## Getting out of your own walls

This is what anchoring is for, and it's the part I find actually interesting.

Next to the per-stream chains, Stratara keeps a second table of anchors. Every so many events it writes down the head of the chain at that point. Each anchor row has a `BlockchainTxHash` column, and that column is the hook: you take the anchor and commit it somewhere you don't control. A public blockchain. An RFC 3161 timestamp authority. An [OpenTimestamps](https://opentimestamps.org/) calendar. A notary. Anything you trust that isn't you.

Once an anchor lives somewhere out of your reach, the recompute attack falls apart. Your insider can rewrite every hash in the database and still can't touch the value you already pinned elsewhere. The question stops being "is this chain internally consistent" and becomes "does it still match what we committed outside." That second one is much harder to fake.

![Image description](https://dev-to-uploads.s3.amazonaws.com/uploads/articles/78ipr0t94yws09abnwki.png align="center")

Let me be straight about what ships versus what you wire yourself. The anchor table, the worker that writes anchors, and the `BlockchainTxHash` column are in the box. Actually pushing an anchor to your source of truth, and checking against it later, is the part you wire up. Stratara doesn't pick the chain for you, the same way it doesn't pick your message broker. The sample at the end runs the whole thing in memory so you can see the shape of it.

One caveat, said plainly: if someone owns your database *and* your anchoring pipeline, they can re-chain and re-anchor and it'll all look fine. The defense only holds if the thing you anchor to is genuinely out of their hands. That's the entire reason to put it outside.

## Where the hashing happens, and where verifying does

The hashing runs on a background worker, not inline on every append, so writes stay cheap. And cheap is measured, not hoped: hashing a typical event takes well under a microsecond — around 700 ns on a *fanless* MacBook Air M4, riding the chip's hardware-accelerated SHA-256 — so the chain worker stays comfortably ahead of a brisk write rate. The chain gets filled in a beat behind the commit. Verifying is a separate thing you do on purpose: a scheduled job, or checking the external anchor. You don't want it on the read path, because that's a `SELECT … ORDER BY Sequence` on every query and it ties each read to the integrity check.

Worth being straight about: nothing in the framework wakes up and hunts for tampering on its own today. The hashes and the anchors are there so that *when* you verify — on a schedule, during an audit, after an incident — the evidence is intact and a break lands on the exact row. For a SOC 2 or ISO 27001 audit, the worker's structured logs are the running record that the hashing happened across the period; the verification job is what proves the chain held.

## Where this lives

I build [Stratara](https://github.com/yesbert/Stratara), a CQRS and event-sourcing stack for .NET 10. The chaining is the `EventStreamHashing` worker, running against Postgres. None of the idea is Stratara-specific though. If you've got an append-only table, you can bolt this on yourself.

The [`TamperProof` sample](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.TamperProof) is the whole story in zero-dependency, in-memory code, in three acts: a clean chain that verifies, a sloppy tamper caught at the exact row, and a full re-chain that sails past the local check but gets caught by an external anchor.

Wiring it into a real app is more than one `dotnet add` — you need the event store, the hashing worker, and a little DI — so the [getting-started guide](https://docs.stratara.tech/getting-started/) walks the minimal setup. Full docs are at [https://docs.stratara.tech](https://docs.stratara.tech), and it's source-available under FSL-1.1-MIT (not OSI-approved OSS), which flips to plain MIT after two years.

This is just one slice of Stratara, and honestly the easiest to show off. There's plenty more I want to write up — the tenant-aware encryption side especially, where a tenant's data is cryptographically bound to their own key — without cramming it all into one wall of text. So if this was your kind of thing, stick around: more coming.

If you're already event sourcing: how would you actually prove to an auditor that nobody's touched the log? Genuinely curious what people are doing here.