# bind-every-documented-options-section

> **Status:** approved (owner, 2026-09-18 — recorded at the owner's request)

## Why

Fourteen framework options types declare the configuration section they belong to. Not all of them
are read from it. `SessionContextOptions` (`SessionContext`) and `ProjectionReplayOptions`
(`ProjectionReplay`) are registered with `AddOptions<T>()` and nothing binds them;
`StrataraBlobEncryptionOptions` (`Stratara:BlobEncryption`), registered by `AddStrataraBlobEncryption()`
and `AddSecurity()`, is bound only when the host also calls `AddStrataraFileKeyStore(configuration)`,
although its own example says the section is read; and `AddStrataraOrleansCommandDispatcher` reads no
`MessageRetry` section on a host that registers no bus transport (found during implementation). So

- `"SessionContext": { "AllowTenantHeader": true }` in `appsettings.json` does nothing, and
- `"ProjectionReplay": { "LeaseSeconds": 600 }` does nothing,

although `docs/concepts/session-context.md` says the first "binds from the `SessionContext`
configuration section" (and, ten lines later, that it does not), and `docs/guides/write-a-projection.md`
shows the second as a JSON block without saying that the host must bind it. The only way to set either
is `services.Configure<T>(…)` in code. And a `LeaseSeconds` of zero or less is accepted: on the
in-process replay state the replay's marking lapses at once and publication resumes in the middle of a
rebuild — the hazard the replay lease exists to prevent.

## What Changes

- **All of them are read from their documented section** — session context, replay, blob encryption,
  and the message retry of an Orleans-only command host — wherever the host registers them — directly or
  through a composite — when the host has an `IConfiguration`; a bare service collection without one
  keeps working. Settings a host configures in code after registering take precedence, as for every
  other option.
- **A replay lease of zero or less is refused at start** with a message naming the setting.
- **A test holds the rule**: every public options type that declares a section name is read from it
  by the registration that adds it.
- **The two documentation pages say one thing.**

**Security-relevant upgrade note.** A host whose `appsettings.json` carries
`"SessionContext": { "AllowTenantHeader": true }` — inert until now — turns the tenant-header fallback on
by upgrading; likewise a `Stratara:BlobEncryption:LegacyBlobsCarryPurpose` entry that was inert takes
effect. The CHANGELOG says so under *Changed*, first line.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `host-composition`: new requirement — a framework setting that documents a configuration section is
  read from it, and an invalid replay lease is refused at start.

## Impact

- Affected specs: `host-composition`
- Affected code: `src/Stratara.Security` (the blob-encryption registration),
  `src/Stratara.Orleans` (`AddStrataraOrleansCommandDispatcher`),
  `src/Stratara.Sessions/DependencyInjection/SessionServiceCollectionExtensions.cs`
  (and `Stratara.Sessions.csproj`: a reference to `Microsoft.Extensions.Options.ConfigurationExtensions`,
  already centrally versioned), `src/Stratara.Outbox.RabbitMQ/DependencyInjection/OutboxServiceCollectionExtensions.cs`
- Tests: `tests/Stratara.Sessions.Tests` (or the existing session tests), `tests/Stratara.Outbox.RabbitMQ.Tests`,
  and a reflection test over the published options types
- Affected docs: `docs/concepts/session-context.md`, `docs/guides/write-a-projection.md`, `CHANGELOG.md`
- Public API: none.
- Schema: none.
- Versioning: part of 4.2.0.
