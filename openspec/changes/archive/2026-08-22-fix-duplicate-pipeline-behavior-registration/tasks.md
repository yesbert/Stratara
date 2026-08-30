# Tasks

- [x] Make `AddPipelineBehavior` / `AddPipelineBehaviorWithResult` idempotent per behaviour type
      (`src/Stratara.Mediator/DependencyInjection/MediatorServiceCollectionExtensions.cs`).
- [x] Verify the four registrars that use them are then idempotent: `AddStrataraValidation`,
      `AddStrataraTenantIsolation`, `AddStrataraResilienceBehavior`, `AddCommandAuditing`.
- [x] Move `TenantIsolationOptions` to the options pattern; resolve `IOptions<TenantIsolationOptions>`
      in `TenantIsolationBehavior` (finding TI-1).
- [x] Test: each registrar called twice installs one behaviour instance.
- [x] Test: a twice-registered validation stage runs each validator once per request.
- [x] Test: `TenantIsolationOptions` binds from configuration.
