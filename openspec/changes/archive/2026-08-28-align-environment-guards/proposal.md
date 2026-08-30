> **Status:** approved

# Align the environment guards on a whitelist

## Why

Three places in the framework ask the environment in order to switch off an unsafe convenience. Two
of them ask differently from the third.

| Guard | Condition | Permitted in |
|---|---|---|
| Development key store | `if (!environment.IsDevelopment()) throw` | **Development only** |
| Broker credential check | `if (hostEnvironment.IsProduction()) throw` | Development, Staging, QA, UAT, Preview, and anything self-named |
| Development email sender | `if (builder.Environment.IsProduction()) throw` | the same |

The first looked like the other two until 3.0.11, when an audit finding inverted it. The reasoning
recorded then was not about cryptography — it was about the shape of the test:

> *"The guard is `if (environment.IsProduction()) throw`. Staging, QA, UAT, Preview, Stage and every
> custom environment name fall through. Many real-world setups run staging and QA against copies of
> production data."*

`IsProduction()` recognises exactly one name. Everything else falls through, including `Production-EU`
and `prod`. That argument applies unchanged to the two guards that were never revisited; they were
left alone because the audit was scoped to key handling.

## What is actually at risk

**The broker guard is the milder case.** Without credentials on staging it falls back to `guest`, and
it **logs** that it did. The exposure is narrower than it sounds: RabbitMQ restricts `guest` to
localhost by default, so a remote broker rejects the connection anyway. The real case is a broker in
the same container or network running a default configuration.

**The email sender is the worse case, because of a chain nobody sees.** It returns
`Task.CompletedTask` for all three operations — no exception, no log:

```csharp
public Task SendConfirmationLinkAsync(TUser user, string email, string confirmationLink) => Task.CompletedTask;
```

On staging a locally registered user therefore never receives a confirmation link, so
`EmailConfirmed` stays false. That collides with a requirement of `external-identity`: auto-linking
an external login to an existing account needs the provider to report the address verified **and**
the local account to be confirmed. So the same user signing in later through an identity provider
gets `RequiresInteractiveLinking` instead of an auto-link.

The symptom somebody reports is *"OIDC linking doesn't work on staging"*. The cause is a silently
dropped confirmation email three steps earlier.

## Decision

**Owner decision, 2026-08-19: narrow both remaining guards to `IsDevelopment()`, and do not add an
opt-in switch.**

No switch is needed because **both escape hatches already exist and are explicit**:

- A host that wants `guest` on staging sets `RABBITMQ_USERNAME=guest`. That makes it a decision
  rather than a fall-through.
- A host that wants mail dropped on staging registers its own `IEmailSender<TUser>` — the three
  lines above.

The guards' leniency adds nothing except an implicit path. Where the framework genuinely needed an
"unsafe by default, some hosts still need it" switch it built one — `SessionContextOptions.AllowTenantHeader`,
fail-closed with an explicit opt-in. Here the configuration *is* the opt-in.

## What Changes

- `RabbitMqBus`: the credential guard becomes `if (!hostEnvironment.IsDevelopment()) throw`.
- `AddDevelopmentNoOpEmailSender<TUser>`: same inversion.
- Two spec deltas, because both capabilities currently specify the lenient behaviour.

Behaviourally breaking for staging hosts relying on either. Patch bump with a migration note is the
precedent — that is exactly how 3.0.11 shipped the key-store inversion, as a CVE-class security fix.

## Impact

- `openspec/specs/outbox-and-messaging/spec.md` — the transport requirement's two credential scenarios.
- `openspec/specs/external-identity/spec.md` — the development-email-sender requirement. Note that
  this requirement's **title already said "cannot run outside development"** while its scenarios
  permitted staging; the backfill recorded the code faithfully in the scenarios and let the title
  overstate it. This change makes the two agree.
