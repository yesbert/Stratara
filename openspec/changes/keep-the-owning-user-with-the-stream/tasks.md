## 1. Reproduce first

- [x] 1.1 In `tests/Stratara.Infrastructure.Tests/EventSourcing/`, against the SQLite test store
  (`EventStoreTestHost`):
  - create a stream under a session whose data-owner user is U;
  - in a later save, under a session naming another user or none, append to it;
  - assert the appended entry records tenant and user U.

  Confirm it fails on `main`, where the entry records no user.
- [x] 1.2 Same place: a stream created through a creation event (tenant, no user), appended to in a later
  save under a session naming user U. Assert the entry records no user. It passes on `main`; it pins
  the rule that the session does not add a user to a stream.
- [x] 1.3 Same place: a stream whose first event was appended on behalf of a stated Subject with tenant T
  and user U, appended to in a later save without one. Assert T and U.

## 2. The fix

- [x] 2.1 In `src/Stratara.Infrastructure/EventSourcing/EventSource.cs`, the stream lookup returns the
  first entry's tenant and user as one Subject, or none when the stream has no entry with a tenant.
  Resolution uses it unchanged. Update the private summary of the resolution order.
- [x] 2.2 Existing tests in `EventSourceTests.cs` that assert a stream owner with no user stay green. Run
  `tests/Stratara.Infrastructure.Tests` in full.

## 3. Documentation

- [x] 3.1 `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs`: the remarks name "the
  owner recorded on the stream's first event — its tenant, and its user where one was recorded"
  instead of the tenant alone.
- [x] 3.2 `docs/guides/write-a-command-handler.md` → *Which tenant owns the events you append*: the same
  in the published guide, with one sentence on the user.
- [x] 3.3 `CHANGELOG.md` → *Unreleased → Fixed*: the entry, including that events recorded since 4.0.0
  without the stream's user stay under the tenant scope, where a user's erasure does not reach them.

## 4. Verify

- [x] 4.1 `openspec validate keep-the-owning-user-with-the-stream --strict` passes.
- [x] 4.2 Local gauntlet green (`./scripts/local-gauntlet.sh`).
