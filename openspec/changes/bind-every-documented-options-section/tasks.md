# Tasks

## 1. Binding

- [ ] 1.1 `AddSessionContext`: lazy bind from `SessionContext`; package reference in `Stratara.Sessions.csproj`.
- [ ] 1.2 `AddProjectionReplayState`: lazy bind from `ProjectionReplay`; `Validate(LeaseSeconds > 0)` and `ValidateOnStart`.
- [ ] 1.3 XML docs of both registrations and both options types state the section and the precedence.

## 2. Tests

- [ ] 2.1 Session: a value from an in-memory configuration arrives; code after the registration wins; no configuration
      registered still resolves defaults.
- [ ] 2.2 Replay: a value arrives; a lease of 0 fails `ValidateOnStart` naming the setting.
- [ ] 2.3 The reflection test over every published options type with a section name (D3).

## 3. Documentation

- [ ] 3.1 `docs/concepts/session-context.md`: one statement, the section and the upgrade note.
- [ ] 3.2 `docs/guides/write-a-projection.md`: the `ProjectionReplay` block is read as shown.
- [ ] 3.3 `CHANGELOG.md` `[Unreleased]` → *Changed*, first line: the security note; *Fixed*: both sections read, the lease
      refused at zero.
