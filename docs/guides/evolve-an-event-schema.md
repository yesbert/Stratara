# Evolve an Event Schema

> **Derived page.** The behaviour described here is specified by the `event-schema-evolution`
> capability under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Events are facts you already wrote down. You cannot change them, so when the shape you need moves
on, you transform them **on the way in** — every time they are read. That transformation is an
*upcaster*.

## One upcaster per schema hop

An upcaster declares the persisted type name it reads from, the type name the payload carries
afterwards, and the rewrite between them:

```csharp
public sealed class OrderPlacedV1ToV2 : IEventUpcaster
{
    public string SourceEventTypeName => "MyApp.Events.OrderPlacedV1, MyApp";
    public string TargetEventTypeName => "MyApp.Events.OrderPlaced, MyApp";

    public JsonNode Upcast(JsonNode payload)
    {
        payload["TotalMinorUnits"] = (int)(payload["Total"]!.GetValue<decimal>() * 100);
        payload.AsObject().Remove("Total");
        return payload;
    }
}
```

```csharp
services.AddEventUpcasterPipeline();
services.AddEventUpcaster<OrderPlacedV1ToV2>();
```

**The source type need not exist any more.** That is what makes a rename possible: the old class is
deleted, only its recorded name survives, and the upcaster matches on that string. Only the *target*
name is ever resolved. Version differences in the assembly-qualified name are ignored, so an
assembly rev does not orphan your history.

**Write one hop, not one per version.** Where one upcaster's target is another's source, the
framework applies them in sequence until nothing matches. Three schema versions need two upcasters,
not three — and the oldest events walk the whole chain on every read.

## What fails, and when

| Mistake | When it bites |
|---|---|
| Two upcasters declaring the same source | **Composition** — the host fails to start, naming the duplicated source |
| A chain that returns to a name it already passed | **Application** — the read fails, naming where the cycle was found |
| An upcaster returning no payload | **Application** — the read fails, naming the upcaster's source |

The first is the good case: an ambiguous registration cannot reach production. The other two are
per-read failures, so a cycle introduced today surfaces the first time an old event is read — which
may be much later.

When no upcaster matches, reading is untouched. Registering the pipeline costs nothing for streams
that never needed it.

## Two boundaries that will catch you out

### An upcaster sees ciphertext

The payload arrives **exactly as it was persisted**. Fields marked for encryption are ciphertext at
that point, because upcasting runs before decryption.

So an upcaster can rename a protected field, move it, or restructure the object around it — all of
that is positional. It **cannot read the value, derive from it, validate it, or split it**. If your
migration needs the plaintext — splitting `FullName` into `First` and `Last`, re-deriving a hash,
range-checking a salary — an upcaster is the wrong tool and no amount of retrying will make it work.
Emit a new corrected event instead, and leave the old one as the fact it is.

### Snapshots are never upcasted

Upcasting runs on every path that reads a recorded event: from the store, and from a message that
crossed the bus, identically. It does **not** run on snapshots.

A snapshot is derived state, not a recorded fact. A snapshot written under an older aggregate shape
therefore **fails to restore rather than being transformed** — deliberately, because silently
reshaping derived state would hide the fact that it no longer matches the events it was derived
from. When you change an aggregate's shape, expect its snapshots to be discarded and rebuilt from
the (upcasted) stream.

## See also

- [Write a Projection](write-a-projection.md) — the read models these events feed.
- [Encrypt Sensitive Data](encrypt-data-setup.md) — what makes a field ciphertext at upcast time.
- [Tamper-Evident Streams](../concepts/tamper-evident-streams.md) — why the stored payload is never
  rewritten in place.
