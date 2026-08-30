# Tasks — A replay marking that nobody is renewing expires

Test-first throughout: each implementation task has a test task before it that fails against the
current code for the stated reason.

Note on where the tests go. `ProjectionReplayState` is exercised in
`tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs` against a
real Redis through `RedisFixture`, and that is where the lease tests belong — an expiry asserted
against a mocked multiplexer would be asserting that the mock was called, not that the key expires.
**That project is excluded from `./scripts/local-gauntlet.sh` and from the pull-request pipeline**, so
these tests must be run deliberately (`dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests`,
needs Docker) and the integration pipeline must be run before the change is called done. Do not read
a green gauntlet as evidence that this change works.

## 1. Cover the lease on the marking

- [ ] 1.1 In `ProjectionReplayStateTests`, add a test that after `Activate()` the active key carries
  an expiry rather than none. Fails today: it is written with no expiry.
- [ ] 1.2 Add a test that after `SetProgress(...)` the processed and total keys carry an expiry.
  Fails today for the same reason.
- [ ] 1.3 Add a test that `SetProgress(...)` extends the active key's remaining lease — activate,
  let the remaining lease fall measurably, report progress, assert the remaining lease went back up.
  Fails today: nothing renews anything.
- [ ] 1.4 Add a test that a marking left standing without renewal reports inactive once its lease has
  lapsed, using a lease short enough to observe. This is the defect in one assertion.
- [ ] 1.5 Add a test that `SetFailed(...)` leaves the recorded error readable with no expiry, so an
  operator still sees why the last replay ended. Passes today and must keep passing.
- [ ] 1.6 Run `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` (Docker required) and
  confirm 1.1–1.4 fail for the stated reasons, not on fixture setup.

## 2. Hold the marking on a lease

- [ ] 2.1 Add a public options type carrying the lease length in seconds, defaulting to 300, in
  `src/Stratara.Outbox.RabbitMQ/Projections/`. Its XML doc states the constraint as a constraint:
  the value must outlast the longest stretch between two progress reports, and a value shorter than
  that lets publication resume while the replay is still running.
- [ ] 2.2 Register the options with their defaults from `AddProjectionReplayState()` so the existing
  registration keeps resolving with no configuration, and take them on `ProjectionReplayState`.
- [ ] 2.3 `Activate()` writes the active key with the lease.
- [ ] 2.4 `SetProgress(...)` writes the progress keys with the lease and refreshes the active key's
  lease in the same call.
- [ ] 2.5 Leave `Deactivate()`, `SetFailed()` and the error key alone — deletion is unaffected by an
  expiry, and the recorded error is deliberately not leased.
- [ ] 2.6 `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` green.

## 3. Confirm the packages that must not change

- [x] 3.1 Confirm `ProjectionReplayWorker` needs no edit: it already calls `SetProgress` once per
  batch, which is the renewal. If it turns out to need one, stop — that contradicts `design.md` and
  the design should be revised before the code is.
- [x] 3.2 `dotnet test tests/Stratara.Projections.Tests tests/Stratara.Outbox.RabbitMQ.Tests` green —
  neither should need a change; a failure here means the lease leaked into behaviour they pin.

## 4. Close out

- [ ] 4.1 `openspec validate expire-an-abandoned-replay-marking --strict` clean.
- [ ] 4.2 `./scripts/local-gauntlet.sh` green — necessary, and explicitly not sufficient here: it
  does not run the tests that cover this change.
- [ ] 4.3 Run the integration-test pipeline (36) on the branch and record the result on the pull
  request, since the pull-request pipeline does not cover this change.
- [ ] 4.4 CHANGELOG entry under the next version, in consumer-visible terms: an abandoned replay no
  longer suppresses publication forever, the lease is configurable, and — the part a consumer must
  act on — a marking already stuck from before adoption is not cleared by upgrading and still needs
  clearing once.
