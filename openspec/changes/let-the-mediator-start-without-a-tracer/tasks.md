## 1. The registration

- [x] 1.1 `src/Stratara.Mediator/DependencyInjection/MediatorServiceCollectionExtensions.cs`:
      `AddMediator()` try-adds a singleton `Tracer` whose factory takes the tracer named
      `ApplicationDiagnostics.Activity.SourceName` from a registered `TracerProvider`, or from
      `TracerProvider.Default` where none is registered. `AddAuthorizingMediator<T>()`
      (`AuthorizationServiceCollectionExtensions.cs`) constructs the mediator itself and applies the
      same try-add, through one shared internal helper. Update both methods' XML remarks: the
      mediator traces every dispatch, a host-supplied tracer wins, the fallback emits under the
      framework's activity source.
- [x] 1.2 Tests in `tests/Stratara.Infrastructure.Tests/DependencyInjection/MediatorServiceCollectionExtensionsTests.cs`:
      `AddMediator()` alone resolves `IMediator` and dispatches a request to its handler; with an
      `ActivityListener` on `Stratara.Application` a dispatch through the fallback produces an
      activity named `Handle <RequestType>`; a `Tracer` registered before `AddMediator()` and one
      registered after are each the instance the mediator uses; `AddAuthorizingMediator<T>()` on its
      own, without `AddMediator()` or a host tracer, resolves and dispatches (the case the
      IdentityDirectory sample exercises). Keep
      `AddMediator_ResolvesToConcreteMediator_WhenDependenciesPresent` as the compatibility pin.

## 2. The places that carried the workaround

- [x] 2.1 Remove the `AddSingleton(TracerProvider.Default.GetTracer(...))` line and its comment from
      `samples/Stratara.Sample.CqrsBasics`, `.EventSourced`, `.OutboxWorker`, `.MoneyTransferSaga`,
      `.AspNetCoreApi`, `.Validation` and `.IdentityDirectory` (`Program.cs` each); drop the
      `OpenTelemetry.Api` package reference from any sample that no longer needs it.
      `dotnet test tests/Stratara.Samples.SmokeTests` proves every sample still runs and prints
      what it printed before.
- [x] 2.2 `README.md` → *Install*, the hello-mediator block: delete the two lines about the tracer;
      the example is `AddMediator()` + handler discovery + dispatch.
- [x] 2.3 `src/Stratara.Mediator/README.md`: delete the registration and its comment; add one
      sentence saying that dispatch spans come from the `Stratara.Application` activity source and
      that a host-supplied `Tracer` is used instead where registered.
- [x] 2.4 `docs/`: confirm no page carries the registration (`grep -rn GetTracer docs`, excluding
      `_site`); `tests/Stratara.Documentation.Tests` stays green.

## 3. Changelog and gate

- [x] 3.1 `CHANGELOG.md` `[Unreleased]` → *Fixed*: `AddMediator()` no longer requires the host to
      register an OpenTelemetry `Tracer`; the fallback emits under `Stratara.Application`; a
      registered tracer is still preferred; the line can be deleted from existing hosts.
- [x] 3.2 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
