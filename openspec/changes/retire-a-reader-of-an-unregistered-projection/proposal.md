# Retire a reader of an unregistered projection

> **Status:** approved

## Why

On the Orleans execution model every store-reading projection keeps one reader per partition, and every reader
keeps a keep-alive reminder. When a host removes a projection, its reminders and checkpoints stay in the store.
On every start of the silo the reminders bring the readers back. Each one reads the next entry of its partition,
fails it because no projection of its name is registered, and tries again on the next tick, without end. The silo
logs a warning per partition and entry, every new fact is read and refused once per partition, and "the logs are
clean" stops working as a check on any host that ever removed a projection.

A saga reader in the same situation already retires, and has since 4.2.0. A projection reader does not.

A consumer reported it on 2026-10-25 under 4.4.8 (consumer finding F-029). After their calendar projection was
removed, its 16 readers logged 1,712 warnings in the first 17 minutes after a start and about 200 a minute after
that.

## What Changes

The effect a consumer sees: a host that removes a projection is left with no reader of it that comes back, reads
and fails. The first time such a reader is brought back, it logs once that it retired and is not brought back again.

- **Projection readers:** a reader brought back on a silo that registers no projection of its name retires when it
  is activated. It unregisters its keep-alive, reads nothing, counts no stall, logs the retirement once at
  Information naming the projection and the partition, and deactivates. A silo that still registers the projection
  — a rolling deployment — can then host it. This is what a saga reader of a removed or renamed saga already does.
- **The projection's checkpoints stay.** A projection registered again later reads on from where it stopped, as a
  saga does. This matches the reset, which leaves alone the checkpoints of a consumer the host does not register.
  The operating guide says how to remove them.
- **Saga readers:** the retirement of a reader of an unregistered saga shipped in 4.2.0 but no requirement records
  it. The new requirement covers both, so this part of the delta records shipped behaviour, with one correction
  below.
- **A retirement is logged once, and its tick is delivered.** Found during implementation: a reader that retires
  as it is activated deactivated before it answered the call that activated it. Orleans forwarded that call to a
  new activation, which retired again, so every retirement was logged three times and the keep-alive tick ended in
  `Could not deliver reminder tick` at Error. This held for the saga reader and for a reader beyond a lowered
  partition count as well. A retired reader now answers the call and deactivates after it (design D6).
- **Unchanged:** the retirement of a reader beyond a lowered partition count. Also unchanged: commit order,
  checkpoints, rebuilds and replays, pauses, waiting for a projection, the reset, and every log event that exists.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md` describes the retirement next to the saga
  reader's, with the log event and how to remove an orphaned projection's checkpoints.
  `docs/reference/log-events-schema.md` gets the new event.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A reader of a consumer the host does not register retires*. A projection or saga
  reader brought back on a silo that registers no consumer of its name unregisters its keep-alive, reads nothing,
  logs that it retired and leaves its checkpoints where they were. A consumer registered again resumes from its
  checkpoint.

## Impact

- **Code:** `Stratara.Orleans`. The projection store reader gets the retirement the saga reader has. The names of
  the projections the silo registers are worked out once per silo instead of on each activation. There is one new
  log event in `Stratara.Diagnostics`. The store reader base grain deactivates a retired reader after the call that
  activated it, which changes every retirement that deactivates. No public type, member or option changes. Grain interfaces and their aliases
  stay as they are, so a rolling deployment mixes silos of both versions.
- **Tests:** `tests/Stratara.Orleans.IntegrationTests`. A host restarted without a projection it ran before has that
  projection's readers retire on their first activation, and nothing is read or refused for them. A host that
  registers the projection again resumes it from its checkpoint. A unit test covers the name lookup.
- **Documentation:** `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/reference/log-events-schema.md` and `CHANGELOG.md`.
- **Release:** a patch (4.4.9). No API is added, and behaviour only gets quieter.
- **Consumer finding:** NextPA `stratara-findings.md`, F-029.
- **Superseded sources:** none.
