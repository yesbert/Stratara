## 1. The guard

- [x] 1.1 Add the refusal to `SecureJsonSerializer`'s encryption path, where the sensitivity level and
      the tenant and user are all in scope, so it covers whole-object and per-field encryption alike.
      It gains `IHostEnvironment` and follows the rule the development key store already uses: an
      error outside development, a warning inside it, and the message names the current environment.
      Verify: the decryption path is untouched — the same helper serves both, and only the caller on
      the write side checks.
- [x] 1.2 Make the message worth reading at the moment it fires: it names the level that was asked
      for, which dimension was missing, and that data with no tenant belongs at the level that claims
      no isolation. Verify: the exception text contains the level name and the word `Confidential`.
- [x] 1.3 Update the three in-repo construction sites the new constructor parameter breaks — the
      benchmark, the unit tests and the smoke test. Verify: `dotnet build Stratara.slnx -c Release`
      is clean.

## 2. Prove it

- [x] 2.1 Tests for the refusal outside development: a level claiming isolation with no identifying
      dimension at all throws, for both the tenant and the user level. Verify: both assert on the
      message, not only the type, so a future reword that drops the guidance fails the test.
      *Narrowed during implementation, on the owner's agreement.* The task originally said "a
      user-scoped value with an empty user", and `ResolvedSubjectEncryptionTests` proved that wrong:
      the framework deliberately encrypts an event's payload at the user level with the stream
      owner's tenant and no user, and asserts it does not decrypt under another tenant. That scope
      resolves to `UserScoped:<tenant>:` and separates tenants — coarser than the level's name, but
      isolation rather than its absence. Two tests now pin the accepted shapes so the rule is not
      widened back by accident.
- [x] 2.2 A test that the same call in development proceeds. Verify: it returns ciphertext rather
      than throwing.
- [x] 2.3 A test that decryption of a value written into the degenerate scope still works outside
      development. **This is the one that matters most** — it is the difference between a guard and
      a data-loss bug. Verify: the round trip succeeds with an empty tenant on the read side.
- [x] 2.4 A test that `Confidential` with no tenant and no user is accepted. Verify: it does not
      throw, because that level claims no isolation.

## 3. Say it where a consumer looks

- [x] 3.1 Document it in the encryption guide: what the levels mean, that a tenant-scoped field on a
      tenant-less aggregate is a mistake rather than a degradation, and that `Confidential` is the
      honest expression for data with no tenant. Verify: a reader who hits the exception finds the
      answer in the guide without reading the source.
- [x] 3.2 Changelog entry under `## [4.0.0] — unreleased`, breaking section: what now fails, why it
      was never doing what it claimed, and that existing data stays readable. Verify: it states the
      migration in one sentence — use `Confidential`.
