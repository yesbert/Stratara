# Tasks

## Decision

- [x] **Owner decision, 2026-08-23: the test-support guard reads the host, not the environment
      name.** The guard inspects the `IServiceCollection` for a registered `IHostEnvironment`, and
      as a second signal `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` when set. It refuses when
      either says something other than Development, and stays silent when neither says anything.
      A true whitelist on the development key store's model was considered and rejected: that store
      is resolved by a *production* composition, where a host — and therefore an environment —
      always exists, so "unset" is not a real case there. Here the legitimate caller is a unit test
      with no host at all, and a whitelist would refuse precisely the correct use. Nothing in this
      repo's pipelines or scripts sets either variable, so it would have broken
      `Stratara.Testing.EntityFrameworkCore.Tests` and `Stratara.EntityFrameworkCore.Tests` on the
      next run, and every consumer's suite on the next upgrade. An explicit opt-in parameter was
      rejected too: it changes a published signature in a lockstep family, and a flag meaning "I
      confirm this is a test" is the kind of thing that gets copied into a host.

- [x] **Owner decision, 2026-08-23: the guard goes in the composition entry point only.**
      `Stratara.Testing` has no composition to guard — it exposes no `IServiceCollection` extension
      at all, and its doubles are plain classes constructed by hand. An environment check in each of
      their constructors would force every unit test to supply an environment: the same collateral
      damage the decision above avoids, at six sites instead of one. The realistic failure is wiring
      the test event store into a host, and that happens at `AddStrataraTestingEventStore`.
      **Accepted consequence:** an `InMemoryMessageBus` constructed by hand in a production host
      still swallows every message and this change does not stop it — the build-time check catches
      the reference before it gets that far.

- [x] **Owner decision, 2026-08-23: yes to the build-time check.** It is the only guard that works
      when no environment is set, it fires at build rather than at start-up, and it is what makes
      the deliberate narrowness of the two decisions above defensible. The criterion is *not*
      `IsPackable` — a consumer's API project is usually `IsPackable=false` too. The invariant is
      that test-support packages belong in test projects.

## Settle first — both block the build-time target

- [x] **How does the target recognise a test project? Answered 2026-08-23: `IsTestingPlatformApplication`,
      with `IsTestProject` accepted as well.** Measured, not assumed: on
      `Stratara.Testing.EntityFrameworkCore.Tests` MSBuild reports `IsTestProject` empty and
      `IsTestingPlatformApplication` true — xunit.v3 runs on the Microsoft Testing Platform and does
      not set the older marker. On `Stratara.Sample.AspNetCoreApi` (an `Exe`, so `OutputType` is
      useless as a discriminator) and on `Stratara.Mediator` both are empty. The target accepts
      either marker, so a consumer on `Microsoft.NET.Test.Sdk` is covered as well as one on the
      testing platform. No `tests/Directory.Build.props` is needed.
- [x] **How does the target get verified? Answered 2026-08-23: a pack-and-consume step in the local
      gauntlet.** The gauntlet already packs all 25 packages into a temporary directory, so the step
      adds a throwaway non-test project that consumes `Stratara.Testing` from that directory as a
      real `PackageReference` and asserts the build fails. This is the only way to exercise the
      target at all — package `build/*.targets` do not flow through the `ProjectReference` the test
      projects here use, so nothing inside the solution can reach it.
- [x] **Evaluate the marker inside a target, not at import time.** Falls out of the two answers and
      is the part that would silently not work: package targets import in an order no single package
      controls, so a condition evaluated while properties are still being assembled can read an
      unset marker and fail a legitimate test project. The check therefore runs in a target
      sequenced before the build, where every property is final.

## Implementation

- [x] `DummyKeyStore.RevokeAsync` and `.EraseScopeAsync` throw, with a message naming the store and
      why (`src/Stratara.Security/DummyKeyStore.cs` — both are `=> ValueTask.CompletedTask` today).
- [x] Guard `AddStrataraTestingEventStore`
      (`src/Stratara.Testing.EntityFrameworkCore/TestEventStoreServiceCollectionExtensions.cs`) on
      the two signals from the decision. Refuse at *registration*, not at first resolve, so the
      failure is immediate and lands on the caller. `HostApplicationBuilder` registers
      `IHostEnvironment` into `builder.Services` before user code runs, so the descriptor is there
      to find.
- [x] Add the build-time target to `Stratara.Testing` and `Stratara.Testing.EntityFrameworkCore`,
      with an escape property (`StrataraAllowTestSupportOutsideTests`) for samples and benchmarks.

## Tests

- [x] Both erasure paths on the development key store fail.
- [x] The composition is refused when a non-Development `IHostEnvironment` is in the collection.
- [x] It is refused when the environment variable says non-Development and no `IHostEnvironment` is
      registered.
- [x] **It still registers when neither signal is present** — the plain unit-test case. This is the
      regression the first decision exists to prevent; without it the guard breaks every suite.
- [x] The two projects that use the test host still pass unchanged
      (`Stratara.Testing.EntityFrameworkCore.Tests`, `Stratara.EntityFrameworkCore.Tests`).

## Rollout

- [x] Both halves are breaking for someone. A consumer calling an erasure path against the
      development store gets an exception where it used to get green; a consumer referencing a
      test-support package from a non-test project gets a failed build. Both belong in the
      CHANGELOG as breaking, with the reason — the point is that the old behaviour was the defect.
- [x] This change is one of the three gating `publish-openspec-to-mirror`.
