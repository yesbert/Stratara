> **Status:** approved

# Fix duplicate pipeline-behaviour registration

## Why

Every pipeline-behaviour registrar in the framework registers with `AddScoped` rather than
`TryAddEnumerable`, so calling one twice installs the behaviour twice. The consequences differ per
registrar and none is benign: every validator runs twice per request, the tenant guard runs twice
against a second conflicting options instance, and a resilient request's retry attempts multiply
rather than add — two nested retry pipelines turn a four-attempt budget into sixteen.

What doubles is the *work*, not what a failure reports. The pipeline is a chain, so the outer stage
throws before the inner one runs and each validation failure is still surfaced once. The cost falls
on the passing path and on any validator with a side effect — a uniqueness check hits the database
twice.

Nothing guards it, nothing documents it, and no test covers it. It was found in validation,
cross-checked in tenant isolation and confirmed in resilience — three instances of one class.

Migration findings **V-2** and **TI-1**.

## What Changes

- Make `AddStrataraValidation`, `AddStrataraTenantIsolation`, `AddStrataraResilienceBehavior` and
  `AddCommandAuditing` idempotent.
- Move `TenantIsolationOptions` onto the options pattern, so it can be bound from configuration and
  a second registration cannot install a conflicting instance.
- Add a test per registrar asserting a double call installs the behaviour once.

Behaviour changes only for a host that calls a registrar twice — which is a defect today.

## Impact

Affected capabilities: `mediator-dispatch` (the registration requirement gains an idempotence
clause), `request-validation`, `tenant-isolation`, `resilience` — no requirement in the latter three
changes, because each already says the stage runs once.
