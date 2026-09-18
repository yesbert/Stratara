# Tasks

## 1. Implementation

- [x] 1.1 `SingletonWorkRegistrations`: the callback per work, later replaces earlier.
- [x] 1.2 `Add<TWork>` stops calling `Configure(configure)` on the shared options.
- [x] 1.3 `SingletonWorkGrain`: its settings = copy of the silo-wide options + its work's callback, validated.
- [x] 1.4 XML docs of both `AddStrataraSingletonWork` overloads: the callback configures this work.

## 2. Tests (`tests/Stratara.Orleans.Tests`)

- [x] 2.1 Two works with 2 and 5 minutes resolve 2 and 5.
- [x] 2.2 One work registered twice with 2 then 5 resolves 5; the service collection equals a single registration's.
- [x] 2.3 `Configure<SingletonWorkOptions>` sets the default for a work without a callback.
- [x] 2.4 An invalid per-work value is refused at start as today.

## 3. Documentation

- [x] 3.1 `docs/guides/operate-the-orleans-execution-model.md`: settings per work and the silo-wide default.
- [x] 3.2 `CHANGELOG.md` `[Unreleased]` → *Fixed* (settings per work) and the note for hosts that relied on the old
      behaviour.
