## 1. Red first

- [x] 1.1 *A context skips `base.ConfigureConventions`*: building the model of `ConventionlessWriteContext` (Npgsql,
  snake_case) throws `InvalidOperationException`, and the message names the context type and
  `base.ConfigureConventions`. This replaces the test that pinned the missing indexes. Record that it fails against
  the current code (`tests/Stratara.EntityFrameworkCore.Tests/StoreSchemaAdditionsTests.cs`, spec scenario *A write
  context keeps the framework's conventions from running*). Result: the three refusal tests failed against the
  unchanged code, and the bridged-consumer diff test stayed green
- [x] 1.2 The same refusal for the design-time model (`IDesignTimeModel`) and with the in-memory provider
  (`StoreSchemaAdditionsTests.cs`)
- [x] 1.3 The bridged-consumer test context calls `base.ConfigureConventions` and declares its own indexes under
  the framework's names beside the convention's. The diff test still yields only the two stream-index drops
  (`StoreSchemaAdditionsTests.cs`)

## 2. Refusal

- [x] 2.1 `WriteDbContext<TContext>`: a private flag set by `ConfigureConventions`, and a check at the start of
  `OnModelCreating` that throws `InvalidOperationException` naming the context and `base.ConfigureConventions`.
  Document the throw with `<exception>` on `OnModelCreating` and update the XML remarks
  (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/WriteDbContext.cs`, design D1)

## 3. Green

- [x] 3.1 1.1 to 1.3 pass, and `tests/Stratara.EntityFrameworkCore.Tests`, `tests/Stratara.WriteStore.Tests`,
  `tests/Stratara.Infrastructure.Tests` and `tests/Stratara.Testing.EntityFrameworkCore.Tests` stay green
- [x] 3.2 The integration classes `EventStore/*` stay green

## 4. Documentation and release notes

- [x] 4.1 `docs/getting-started/di-composition.md`: the sentence on `ConfigureConventions` says that a context which
  does not call `base` is refused when its model is built
- [x] 4.2 `CHANGELOG.md`, section 4.5.0: the upgrade note on `ConfigureConventions` says that the model is refused,
  with the message's remedy

## 4a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 4a.1 The 4.5.0 summary no longer says "No API change" without qualification. It names the refusal of a
  context that skips `base.ConfigureConventions`, and the upgrade note no longer describes behaviour that was never
  released (`CHANGELOG.md`)
- [x] 4a.2 The message says that the write store's conventions did not run, then what an override must call, and
  names the context by its full name (`WriteDbContext.cs`)
- [x] 4a.3 The bridged-consumer test context takes the framework's convention out again, so it models a snapshot
  from before the upgrade, and a test proves the removal (`StoreSchemaAdditionsTests`)
- [x] 4a.4 Answered without a change: sealing `ConfigureConventions` behind a new virtual hook (design D2); a context
  that replaces `base.OnModelCreating` and applies the store's configurations itself owns its model, as before

## 5. Verification

- [x] 5.1 `openspec validate refuse-a-write-context-that-skips-its-conventions --strict`
- [x] 5.2 `./scripts/local-gauntlet.sh` green
