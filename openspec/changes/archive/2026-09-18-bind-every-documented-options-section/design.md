## Context

`AddSessionContext()` (`SessionServiceCollectionExtensions.cs:33`) and `AddProjectionReplayState()`
(`OutboxServiceCollectionExtensions.cs:78`) call `AddOptions<T>()` and nothing more. The composites have
`builder.Configuration` in hand but do not pass it (`WorkerDefaultsHostBuilderExtensions.cs:177,308`).
`AddOutboxDispatcher` already binds lazily from whatever `IConfiguration` the container holds
(`OutboxServiceCollectionExtensions.cs:40-42`), and `MessagingServiceCollectionExtensions.cs:45-47`
validates on start.

## Goals / Non-Goals

**Goals:** the unbound sections read (session context, replay, blob encryption, the Orleans-only
message retry); an impossible lease refused; a test that keeps the next options type
from joining them.

**Non-Goals:** validating every other option (each already has what its capability states); binding
options that document no section (the Orleans options are configured in code by design).

## Decisions

### D1 — Bind lazily from the container's configuration, once

**Decision.** Each registration adds an `IConfigureOptions<T>` class that reads the section from
whatever `IConfiguration` the container holds when the options are first resolved, registered with
`TryAddEnumerable` so it exists once, at the position of the first registration. No signature changes;
a service collection without an `IConfiguration` binds nothing. Validation is an `IValidateOptions<T>`
registered the same way, with `ValidateOnStart()`.

**Why not `Configure<IServiceProvider>(…)` as `AddOutboxDispatcher` does** (the plan until
implementation, 2026-09-18). Every call would add another configure action. The session context is
registered by `AddCommonFrameworkServices`, which every composite calls; a host that combines
composites, or calls a registration twice, would have the section applied again *after* a value it set
in code in between — for `AllowTenantHeader`, a security setting silently overridden. Evidence: the
repeat-registration tests in `SessionServiceCollectionExtensionsTests` and
`ProjectionReplayOptionsBindingTests`.

**Why not `BindConfiguration`.** It resolves `IConfiguration` unconditionally and throws where none is
registered — a unit test's bare `ServiceCollection`, which the framework's own tests and consumers use.

**Why not bind in the composites.** A host that calls `AddSessionContext()` directly would stay
unbound, which is the defect.

**Precedence.** Options configure actions run in registration order; a host's `Configure<T>` after the
registration runs after the bind and wins. One registered before it is overwritten for keys present in
the section — the same as every other bound option in the framework, and stated in the XML docs.

### D2 — `Stratara.Sessions` gains one package reference

`Microsoft.Extensions.Options.ConfigurationExtensions` (already in `Directory.Packages.props`,
already referenced by `Stratara.Outbox.RabbitMQ`). It is a Microsoft.Extensions package with no web
dependency; the tier rule is unaffected.

### D3 — The rule as a test

A test enumerates the public types in the published assemblies that declare a `SectionName` (or
`DefaultSectionName`) constant, registers each through the registration that owns it on a container
with an in-memory configuration setting one property, and asserts the value arrives. A new options
type that is not bound fails it.

## Risks / Trade-offs

- [An inert `AllowTenantHeader: true` or `LegacyBlobsCarryPurpose` in a consumer's settings becomes
  live] → the first line under *Changed* in the CHANGELOG, and the upgrade notes of the session-context
  and encryption pages.
- [`IOptionsMonitor` does not reload these options when configuration changes] → as for every lazily
  bound option in the framework today; not in scope.
