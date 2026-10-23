## 1. The pool size

- [x] 1.1 `EnsureMaxPoolSize` applies the default only where the connection string names no maximum pool size, and reports which applied (`NpgsqlDbContextServiceCollectionExtensions.cs`)
- [x] 1.2 Log event `PoolSizeInEffect` (`102_008`, Information, category `Stratara.EventSourcing.EntityFrameworkCore.ConnectionPool`) once per host and database, naming the size, its origin, the database and the host, never the password (`src/Stratara.Diagnostics/LogEvents.cs`, `PoolSizeReport.cs`; the log reference lists the `102_000` range only)
- [x] 1.3 Unit tests: `Maximum Pool Size=100`, `MaxPoolSize=100` and lowercase keep 100; 40, 1 and 2 are kept; no pool size gives 15; a registered context connects with the named size; the log names the size and its origin once per host and database, under its own category, never with the password; each host reports for itself; a line a log level suppressed is written once the level allows it (`tests/Stratara.EntityFrameworkCore.Tests/DependencyInjection/PoolSizeTests.cs`)

## 2. Documentation and release notes

- [x] 2.1 `docs/getting-started/di-composition.md` (connection pools), `operate-the-orleans-execution-model.md` (sizing a silo's pool) and the DI cheatsheet: the default, why it is 15, the shared pool per connection string
- [x] 2.2 `CHANGELOG.md`: entry under `[Unreleased]`

## 3. Verification

- [x] 3.1 `openspec validate let-a-consumer-choose-the-pool-size --strict`
- [x] 3.2 `./scripts/local-gauntlet.sh` green
