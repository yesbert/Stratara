# Tasks

## 1. Binding

- [x] 1.1 `AddSessionContext`: lazy bind from `SessionContext`; package reference in `Stratara.Sessions.csproj`.
- [x] 1.2 `AddProjectionReplayState`: lazy bind from `ProjectionReplay`; `Validate(LeaseSeconds > 0)` and `ValidateOnStart`.
- [x] 1.3 XML docs of both registrations and both options types state the section and the precedence.
- [x] 1.4 `AddStrataraBlobEncryption` (and so `AddSecurity`): lazy, run-once bind from `Stratara:BlobEncryption`;
      `AddStrataraFileKeyStore`'s own configuration keeps precedence. Test: `BlobEncryptionOptionsBindingTests`
      (`tests/Stratara.Security.Tests`) and the `StrataraBlobEncryptionOptions` cases of `OptionsSectionBindingTests`.
- [x] 1.5 `AddStrataraOrleansCommandDispatcher`: lazy, run-once bind from `MessageRetry` with start-up validation, added
      only where no bus transport registered before it reads the section. Test: `MessageRetryBindingTests`
      (`tests/Stratara.Orleans.Tests`) and the `MessageRetryOptions via AddStrataraOrleansCommandDispatcher() without a bus`
      case of `OptionsSectionBindingTests`.

## 2. Tests

- [x] 2.1 Session: a value from an in-memory configuration arrives; code after the registration wins; no configuration
      registered still resolves defaults.
- [x] 2.2 Replay: a value arrives; a lease of 0 fails `ValidateOnStart` naming the setting.
- [x] 2.3 The reflection test over every published options type with a section name (D3).

## 3. Documentation

- [x] 3.1 `docs/concepts/session-context.md`: one statement, the section and the upgrade note.
- [x] 3.2 `docs/guides/write-a-projection.md`: the `ProjectionReplay` block is read as shown.
- [x] 3.3 `CHANGELOG.md` `[Unreleased]` → *Changed*, first line: the security note; *Fixed*: both sections read, the lease
      refused at zero.
