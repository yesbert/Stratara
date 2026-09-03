# Design — Let the mediator start without a tracer

## Context

See `proposal.md` → *Why*. What matters here is how the mediator obtains its tracer today and what
the framework already owns that can stand in.

`Stratara.Mediator.Mediator` (`src/Stratara.Mediator/Mediator.cs`) takes an OpenTelemetry `Tracer`
through its constructor and wraps each dispatch in `tracer.StartActiveSpan($"Handle {requestType}")`.
`AddMediator()` (`src/Stratara.Mediator/DependencyInjection/MediatorServiceCollectionExtensions.cs`)
registers `IMediator` and the authorization startup validator, and nothing else. No package in the
family registers a `Tracer`; `Stratara.ServiceDefaults` configures a `TracerProvider` through
OpenTelemetry's hosting integration and subscribes it to the host's application name and to
`ApplicationDiagnostics.Activity.SourceName` (`OpenTelemetryExtensions.cs:103`), but a `Tracer`
instance is a separate service that every host has registered by hand — the samples with
`TracerProvider.Default.GetTracer("<sample name>")`, each under its own name.

`Stratara.Mediator` already references `Stratara.Diagnostics` (Tier-A), which owns the framework's
single `ActivitySource`, named `Stratara.Application`. The observability capability requires that
every trace the framework emits come from that one source. The mediator's spans are the one place
where that is only true if the host happens to name its tracer accordingly.

A `Tracer` in OpenTelemetry's API is a thin handle over an `ActivitySource` of the same name: the
span it starts is an `Activity`, and whether the activity is actually created depends on whether a
listener is subscribed to that source name. A tracer nobody listens to costs a sampling check per
dispatch and allocates nothing.

## Goals / Non-Goals

**Goals:**

- `AddMediator()` alone yields a resolvable `IMediator`.
- A host that registered a tracer before this change observes no difference after it.
- The fallback conforms to the observability capability: its spans come from the framework's
  single activity source.

**Non-Goals:**

- Replacing the injected `Tracer` with direct use of `ApplicationDiagnostics.Activity.Source`. That
  would be the cleaner architecture and would make the observability requirement hold
  unconditionally, but it moves every existing host's dispatch spans from the source they named to
  the framework's, which silently empties a dashboard filtered on the old name. Not in a patch
  release; recorded below as the alternative to revisit.
- Making dispatch spans configurable, nameable or suppressible. Out of scope; nothing asked for it.

## Decisions

### The fallback is registered by `AddMediator()` with try-add semantics, under the framework's source name

`AddMediator()` — and `AddAuthorizingMediator<T>()`, which constructs the inner mediator itself
without requiring `AddMediator()` to have run, as the IdentityDirectory sample shows — calls
`TryAddSingleton<Tracer>` with a factory that asks a registered
`TracerProvider` for a tracer named `ApplicationDiagnostics.Activity.SourceName` and, where no
provider is registered, asks `TracerProvider.Default` for the same. Try-add is what makes the
compatibility promise: a host that registered its own `Tracer` — before or after `AddMediator()`,
since try-add only checks the service type — keeps it. The source name is what makes the fallback
conform: a host on `Stratara.ServiceDefaults` already subscribes to that name, so its dispatch spans
appear the moment it deletes its own registration, and a host that subscribes to nothing gets an
unlistened source, which is the documented no-cost case.

Preferring a registered `TracerProvider` over the default one is a small correctness point: a host
that configured OpenTelemetry through the hosting integration owns a provider with its own resource
attributes and processors, and a tracer taken from that provider participates in them.
`TracerProvider.Default` is the right answer only where there is no provider at all.

Evidence: `MediatorServiceCollectionExtensionsTests` — the existing
`AddMediator_ResolvesToConcreteMediator_WhenDependenciesPresent` pins that a host-supplied tracer
still resolves; the new tests pin resolution without one, the source name of the fallback's spans
through an `ActivityListener`, and that a host-supplied tracer is the one used.

*Rejected: make the `Tracer` constructor parameter optional (`Tracer?`) and skip the span when it is
null.* Two code paths through every dispatch for the rest of the mediator's life, and a host that
subscribes to framework telemetry would still see no dispatch spans unless it registered a tracer
by hand — the fallback would fix the failure but not the gap.

*Rejected: register the fallback from `Stratara.ServiceDefaults` or the worker composites instead.*
The failing host is precisely the one that references neither. The registration has to live where
the dependency is declared.

*Deferred, not rejected: have the mediator use `ApplicationDiagnostics.Activity.Source` directly and
drop the `Tracer` dependency.* This is what the observability capability's single-source requirement
actually asks for, and it removes the `OpenTelemetry.Api` reference from the mediator's own code
path. It changes the source name of existing hosts' dispatch spans, so it is a minor-version change
with a changelog entry telling operators to add one source name to their subscription. It should be
proposed on its own when the next minor is open.

### The samples lose the line rather than keeping it as documentation

Each sample currently carries the registration with a comment saying the mediator needs it. After
this change the comment is false and the line is noise in exactly the place a newcomer reads first.
The samples' smoke tests (`tests/Stratara.Samples.SmokeTests`) run each sample and assert its
output, so removing the line is proven by the existing suite.

## Risks / Trade-offs

- [A host registered its `Tracer` through a mechanism try-add does not see — a keyed service, a
  factory registered after the container is built] → Not a supported way to supply a constructor
  dependency today either; such a host already fails. No new exposure.
- [A host that relied on the failure as a reminder to configure telemetry] → Nothing relied on a
  dependency-injection exception as a linter. The package README says what to subscribe to.
- [The fallback tracer is created per host, not shared with `ApplicationDiagnostics.Activity.Source`]
  → Two `ActivitySource` instances with the same name are indistinguishable to a listener, which
  matches by name. No consumer can observe the difference.
