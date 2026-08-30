# Tasks

## Implementation

- [x] `RabbitMqBus`: invert the credential guard to `if (!hostEnvironment.IsDevelopment()) throw`
      (`src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs`, around the `guest` fallback).
      Keep the message naming the current environment name — it is what tells an operator their host
      is not in the environment they assumed.
- [x] `AddDevelopmentNoOpEmailSender<TUser>`: same inversion
      (`src/Stratara.Identity.AspNetCore/DependencyInjection/AspCoreIdentityHostBuilderExtensions.cs`).
- [x] Check whether any third guard of this shape exists that the backfill did not reach: search for
      `IsProduction()` across `src/` and decide each hit. The two here were found by specifying two
      unrelated capabilities, which is not a systematic search.

      **Result: three hits, one of them new.** The two this change fixes, plus
      `BusEnvelopeIntegrityStartupProbe` (`src/Stratara.Infrastructure/Security/Integrity/`), which
      reads `if (mode == Off && environment.IsProduction())` and emits a warning.

      **Decision: real, but not fixed here.** It is a different kind of guard — it warns rather than
      refusing, so nothing is permitted or prevented either way. It does share the blacklist miss: a
      host named `Production-EU` or `prod` is production and gets no warning that envelope integrity
      is off. Inverting it to `!IsDevelopment()` would also warn on every staging and QA host, which
      is a behaviour change in the `bus-envelope-integrity` capability and needs its own delta —
      a third capability in a change scoped to two.

      It belongs to `harden-bus-envelope-against-replay`, which is approved, is about exactly this
      mechanism, and will be revisiting the mode surface anyway. Carried there rather than left
      unrecorded.

## Tests

- [x] Replace `PublishAsync_StagingEnvironment_MissingCredentials_DoesNotThrowInvalidOperation` with
      its inverse; keep the Development case.
- [x] Replace `AddDevelopmentNoOpEmailSender_InStagingEnvironment_DoesNotThrow` with its inverse;
      keep the Development and Production cases.
- [x] Add one case per guard for an unrecognised environment name (`Preview`, `prod`), which is the
      class the blacklist shape actually missed.

## Release

- [x] Migration note in `CHANGELOG.md`: behaviourally breaking for staging hosts relying on either
      fall-through, with the two explicit escape hatches named — `RABBITMQ_USERNAME=guest`, and
      registering an own `IEmailSender<TUser>`.
- [x] Patch bump, as a security fix. Precedent: 3.0.11 shipped the key-store inversion the same way.
