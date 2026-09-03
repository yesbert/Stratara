> **Status:** approved

# Let the mediator start without a tracer

## Why

The smallest possible Stratara host — one package, one `AddMediator()`, one handler — does not
start. Every dispatch is traced, and the mediator obtains its tracer from the host's service
provider, but nothing in the framework registers one. A host that follows the package README and
calls `AddMediator()` fails at the first resolve of `IMediator` with a dependency-injection error
about a type it never heard of. The seven samples, the README's "hello mediator" and the package
README all carry the same extra line to work around it, each with a comment explaining why.

For a consumer who arrives looking for a mediator, that line is the first thing they see and it says
"this framework needs OpenTelemetry before it does anything". It does not: the tracer is there so
that a host *with* telemetry sees each dispatch as a span. A host without telemetry should simply
dispatch. This is the first step of the entry-point work approved on 2026-09-03 — the mediator-only
door has to be one package and no ceremony — and it is also a plain defect: a registration that is
required for the framework to function is the framework's to make.

## What Changes

- **`AddMediator()` is sufficient on its own.** A host that registers the mediator and nothing else
  dispatches requests. Tracing degrades to a source nobody listens to rather than to a failure.
- **A host that brings its own tracer keeps it.** The fallback is only used where the host has not
  registered one, so no existing host changes behaviour by upgrading.
- **The fallback emits under the framework's own activity source.** A host that has no tracer of its
  own but subscribes to Stratara telemetry — as the observability capability tells it to — sees the
  dispatch spans under that source. Today those spans exist only if the host builds a tracer itself.
- The samples, the repository README and the package README drop the workaround line and its
  comment.

Nothing about what is dispatched, in what order the pipeline runs, or what a span is named changes.
A host that registers a tracer today produces the same spans tomorrow.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

- `mediator-dispatch`: the requirement *Every dispatch is traced* gains the guarantee that tracing
  never becomes a precondition for dispatch — a host that supplies no tracer still dispatches, a
  host that supplies one keeps it, and the framework's fallback emits under the framework's single
  activity source.

## Impact

- `src/Stratara.Mediator/DependencyInjection/MediatorServiceCollectionExtensions.cs` — `AddMediator()`
  registers the fallback tracer with try-add semantics.
- `src/Stratara.Mediator/DependencyInjection/AuthorizationServiceCollectionExtensions.cs` —
  `AddAuthorizingMediator<T>()` constructs the mediator itself and applies the same registration.
- `src/Stratara.Mediator/README.md` and `README.md` — the workaround line and its comment go.
- `samples/Stratara.Sample.{CqrsBasics,EventSourced,OutboxWorker,MoneyTransferSaga,AspNetCoreApi,Validation,IdentityDirectory}/Program.cs`
  — the same line goes from each; the smoke tests prove they still run.
- `tests/Stratara.Infrastructure.Tests/DependencyInjection/MediatorServiceCollectionExtensionsTests.cs`
  — the existing test that registers a tracer first is joined by the cases the delta names.
- `CHANGELOG.md` — `[Unreleased]`.
- Additive on the published surface: a patch release. No consumer has to change anything; a consumer
  may delete a line.
