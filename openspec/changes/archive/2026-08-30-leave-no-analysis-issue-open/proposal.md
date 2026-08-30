> **Status:** approved

# Leave no analysis issue open

## Why

Nothing a consumer can observe is wrong. Every issue below is a convention, a readability hint or a
false positive; no package behaves differently because of any of them, and none is a defect in the
framework's contract.

What is wrong is that they were invisible. The nightly analysis has been reporting them for months
while its pipeline reported success, because the step that fetched the quality gate did not fail the
build on it. The first run through the new analysis workflow waits for the gate, and it comes back
**FAILED**: 45 violations against a threshold of 0. Coverage on new code is 90.1 % and duplication
0.4 %, both comfortably inside their thresholds — the violation count is the only thing standing
between this project and a green gate.

**The project's answer is zero open issues**, not a narrower definition of which ones count. An issue
is outstanding until it is dealt with, whenever it arose. The server's new-code period is therefore
deliberately left alone: moving it would lower the number without removing a single finding.

## What Changes

- **30 assertions stop re-deriving what they were just handed.** `Assert.Single(x)` returns the
  element; the tests then indexed `x[0]` for it anyway. Using the return value is shorter and says
  what it means.
- **Three tests state the expectation they were only implying.** They assert that a startup guard
  accepts a well-formed aggregate, and did so by calling it and letting the absence of an exception
  be the verdict. `Assert.Null(await Record.ExceptionAsync(...))` makes that the visible claim.
- **Four regular expressions move to `[GeneratedRegex]`**, so they are built at compile time rather
  than parsed on first use.
- **Four overload groups are made adjacent**, one parameter is renamed to match the base class it
  overrides, and three loops become the `Where` they were spelling out.
- **Three findings are suppressed at the site, with a reason.** Two are text inside an exception
  message that names the environment variables a host must set — the analyser reads
  `RABBITMQ_PASSWORD=guest` in that sentence as a credential. The third is a performance hint that
  contradicts the project's own rule that a public surface exposes `IReadOnlyList<T>`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. No requirement changes: this change alters how code is written, never what it guarantees.
`.openspec.yaml` sets `skip_specs: true`.

## Impact

**Deliberately out of scope: the six deprecation reminders.**

`S1133` — *"do not forget to remove this deprecated code someday"* — fires on `[Obsolete]` members of
published API:

- `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotRepository.cs:52,62`
- `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs:50,58`
- `src/Stratara.Infrastructure/DependencyInjection/AuthorizationExceptionApplicationBuilderExtensions.cs:28`
- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs:49`

Removing them is a breaking change, and the owner decided on 2026-08-30 to carry it with `4.0.0` —
a major is already under consideration for an unrelated dependency, and one break costs a consumer
less than two. They are **not** suppressed here: while the deprecation is live, that reminder is the
only thing keeping it visible. Until `4.0.0` ships, the gate shows exactly these six and nothing
else, which makes the number itself a reminder with a date on it.

**A mechanism that does not work.** `sonar.issue.ignore.multicriteria` in
`.github/workflows/sonar.yml` lists `CA1859` with a resource key of `**`, and `CA1859` appears in the
report regardless. The list came over unchanged from the Azure pipeline, so it was presumably just as
inert there. Whatever this change suppresses, it suppresses at the site in source, where the reason
sits next to the code and a reader can see it.

**Affected:** 32 files, almost all under `tests/`. On the source side, nine:

- `src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs` — `[SuppressMessage]` for the two `S2068`
- `src/Stratara.Infrastructure/EventSourcing/AggregateSnapshotShapeGuard.cs` — `[SuppressMessage]` for `S3011`
- `src/Stratara.Identity.EntityFrameworkCore/EfApiKeyStore.cs` — `CA1859`, parameter type
- `src/Stratara.Identity.EntityFrameworkCore/IdentityDirectoryDbContext.cs` — `S927`, parameter name
- `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotRepository.cs` and
  `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs` —
  `S4136`, overload ordering only
- `src/Stratara.Mediator/Authorization/AuthorizingMediator.cs`,
  `src/Stratara.Infrastructure/Authorization/AuthorizingCommandOutboxDispatcher.cs` and
  `src/Stratara.Abstractions/Authorization/PermissionCatalog.cs` — `S3267`, the three permission loops

No public signature changes, so no consumer recompiles anything and no version bump follows from this
change.

**Two things went differently than this proposal expected, and the archive is where that is
recorded.**

`CA1859` was **fixed rather than suppressed.** This proposal assumed it conflicted with the project's
`IReadOnlyList<T>` rule; it does not, because that rule is about public surfaces and the finding is on
a private static helper. The single caller hands it a `List<Guid>` straight out of `ToListAsync`, so
the parameter type was the fix and no suppression was needed. Only two suppressions exist, both in
`RabbitMqBus.cs` and `AggregateSnapshotShapeGuard.cs`.

The three permission loops needed **two passes.** Rewriting them as `foreach (… .Where(…)) { throw; }`
satisfied `S3267` and immediately raised `S1751` — the body exits unconditionally, so the loop never
runs twice — and the first analysis run after the merge read **9** rather than 6. `FirstOrDefault`
removed the loop and closed both rules, in the follow-up `fix/close-the-loop-findings`. This is the
risk `design.md` named under *"A rule fires on the replacement"*, and it is the reason the
verification in task 7.3 is a number and not a feeling.
