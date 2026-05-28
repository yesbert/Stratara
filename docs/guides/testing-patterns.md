# Testing Patterns

Stratara tests use **xUnit v3** on **Microsoft Testing Platform (MTP)**. The framework + consumer apps share the same conventions.

## Project layout

```
tests/
├── Stratara.{Package}.Tests/             — unit tests, fast, no Docker
├── Stratara.{Package}.IntegrationTests/  — Testcontainers (Postgres / RabbitMQ); skipped by local-gauntlet
├── Stratara.SmokeTests/                  — composition-/lifecycle tests, console-runner
└── Stratara.Samples.SmokeTests/          — runs each sample as a subprocess, asserts on stdout
```

The `*IntegrationTests` suffix is a CI boundary — `local-gauntlet.sh` + `azure-pipelines-publish.yml` skip them; Pipeline 36 runs them.

## Run a single project

```bash
dotnet test tests/Stratara.Shared.Tests
```

## Run the full local gauntlet

```bash
./scripts/local-gauntlet.sh
```

Builds the full repo (`Stratara.slnx`), runs every unit test project, then `dotnet pack`s every packable project as a sanity check.

## Test conventions

- **`[Fact]` over `[Theory]`** unless there's genuine data-table variation. A `[Theory]` with 2 rows is usually 2 `[Fact]`s in disguise.
- **`Mock<ILogger>`** from Moq — Stratara doesn't bring its own logger fake.
- **`[ExcludeFromCodeCoverage]`** on pure data classes (DTOs, events, commands/queries, configs). The coverage report should reflect *executable behaviour*, not record-derived getters.
- **`[UsedImplicitly]`** on framework-invoked members (Apply methods, projection handlers, JSON-deserialized setters) — ReSharper / Rider can't see the reflection-driven call sites.
- **No code comments in test bodies.** If a test needs explanation, the explanation goes in the *test name*. `LogChangeSetCreated_DefersFieldNameJoinWhenDebugDisabled` — full sentence, scenario + expected outcome.

## Mocking handlers (the unified `IQueryHandler<TRequest, TResult>`)

Both `ICommand<TResult>` and `IQuery<TResult>` share `IRequest<TResult>` + handle via `IQueryHandler<TRequest, TResult>`. Mock that interface, not `ICommandHandler<T>`:

```csharp
var handler = new Mock<IQueryHandler<MyCommand, Guid>>();
handler.Setup(h => h.HandleAsync(It.IsAny<MyCommand>(), It.IsAny<CancellationToken>()))
       .ReturnsAsync(Guid.NewGuid());
```

## Mocking `SignInManager<TUser>` / `UserManager<TUser>`

Castle.DynamicProxy (Moq's underlying engine) needs **public** TestUser classes. Internal types fail with `ArgumentException: type is not accessible`.

```csharp
public sealed class TestUser : IdentityUser   // public, not internal
{
}
```

## Logger-extension testing

Stratara's source-gen `[LoggerMessage]` extensions all live under `Stratara.Shared.Diagnostics.Extensions` (cross-package convention, intentional). Test the logger setup, not the format string:

```csharp
var logger = new Mock<ILogger>();
logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

logger.Object.LogMyEvent(eventId: 1, message: "hello");

logger.Verify(
    l => l.Log(LogLevel.Information, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
    Times.Once);
```

## Integration-test boundary

If your test:

- Needs Docker (a real Postgres / RabbitMQ / Service Bus emulator) → `*IntegrationTests` project.
- Needs only an in-memory store / mock → regular `*Tests` project.

Don't mix. The CI separation depends on the suffix.
