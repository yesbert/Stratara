## 1. The registrations

- [x] 1.1 `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs`:
      `AddNpgsqlWriteDbContextFactory<TDbContext>()` try-adds a scoped `IWriteUnitOfWork` built as
      `WriteUnitOfWork<TDbContext>` from the context factory, `ISessionContextProvider` and
      `ISecureJsonSerializer`, all resolved inside the factory delegate. Update the XML summary and
      remarks: what is registered, that a consumer-supplied unit of work wins, that the session and
      security composites supply the other two dependencies.
- [x] 1.2 Same file: `AddNpgsqlReadDbContextFactory<TDbContext>()` try-adds a scoped
      `IProjectionsUnitOfWork` as `ProjectionsUnitOfWork<TDbContext>` and a scoped `IReadUnitOfWork`
      that resolves to it. XML docs likewise.
- [x] 1.3 Tests in `tests/Stratara.EntityFrameworkCore.Tests/DependencyInjection/NpgsqlDbContextServiceCollectionExtensionsTests.cs`
      (new): with a write context registered and the session/security doubles present,
      `IWriteUnitOfWork` resolves as `WriteUnitOfWork<TContext>` and is scoped; a consumer
      `IWriteUnitOfWork` registered before, and one registered after, is the instance resolved;
      registering the context twice yields one `IWriteUnitOfWork` descriptor; the read side resolves
      `IProjectionsUnitOfWork` and `IReadUnitOfWork` to the same `ProjectionsUnitOfWork<TContext>`
      instance within a scope, with the same precedence cases. Resolution only — no database is
      opened.

## 2. Documentation and generated inventory

- [x] 2.1 `docs/reference/di-extensions-cheatsheet.md` rows 123–124: the write row describes the
      try-added `IWriteUnitOfWork`; the read row says `IProjectionsUnitOfWork` / `IReadUnitOfWork`.
- [x] 2.2 `src/Stratara.EventSourcing.EntityFrameworkCore/README.md`, where the consumer contexts are
      shown: the factory registration is all the store needs. (`di-composition.md` shows no consumer
      context, so it has nothing to say here.)
- [x] 2.3 Regenerate `llms-full.txt` with the generator the documentation tests use
      (`tests/Stratara.Documentation.Tests`), so the registration inventory carries the new summaries.
- [x] 2.4 `CHANGELOG.md` `[Unreleased]` → *Fixed*: registering the write or read context now
      registers its unit of work; a consumer-supplied one still wins; the hand-written line can go.

## 3. Gate

- [x] 3.1 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
