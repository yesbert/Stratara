# Guides

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

Task-oriented how-tos for the most common Stratara operations. Each guide assumes you've worked through **[Getting Started](../getting-started/index.md)** and at least the first sample.

## Domain wiring

- **[Write a Command Handler](write-a-command-handler.md)** — `ICommandHandler<T>` + DI registration.
- **[Write a Projection](write-a-projection.md)** — read-side stores driven by event bundles.
- **[Write a Saga](write-a-saga.md)** — process managers that fan one event into many commands.

## Pipeline behaviors

- **[Write a Validator](write-a-validator.md)** — `IValidator<T>` that runs before the handler.
- **[Enforce Tenant Isolation](enforce-tenant-isolation.md)** — reject cross-tenant requests at the mediator entrance.

## Security

- **[Encrypt Sensitive Data](encrypt-data-setup.md)** — `[EncryptData]` + AES-GCM + tenant-aware AAD.
- **[Authorization Decorators](auth-decorators.md)** — `[RequireRole]` + `AuthorizingMediator`.
- **[Bus-Envelope Integrity (HMAC)](hmac-bus-envelope.md)** — opt-in tamper protection on the message bus.

## Identity & access

Who the caller is, which tenant they act in, and what they may do there.

- **[Tenant Membership](tenant-membership.md)** — many-to-many user↔tenant with per-membership roles, and the `stratara:tenant_id` sign-in bridge.
- **[Permission-Based Authorization](require-permission.md)** — `[RequirePermission]` + the code-first permission catalog.
- **[Scoped Settings](scoped-settings.md)** — global / tenant / user / user-in-tenant values with a fixed fallback chain.
- **[API Keys and Personal Access Tokens](api-keys-and-pats.md)** — machine callers on the same authorization plane as humans.
- **[External Login (OpenID Connect) + JIT Provisioning](external-login-oidc.md)** — link by issuer `sub`, fail-closed against nOAuth-class takeover.

## Infrastructure

- **[Outbox — RabbitMQ](outbox-setup-rabbitmq.md)** — broker setup + worker wiring.
- **[Outbox — Azure Service Bus](outbox-setup-azureservicebus.md)** — managed-identity setup.
- **[Configure Snapshots](configure-snapshots.md)** — `ISnapshotStrategy` + when to deviate from the version threshold.

## Test discipline

- **[Testing Patterns](testing-patterns.md)** — xUnit v3 MTP idioms, integration-test boundary, test-fakes.
