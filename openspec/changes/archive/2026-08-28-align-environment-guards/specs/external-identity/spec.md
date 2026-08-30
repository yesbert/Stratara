## REMOVED Requirements

### Requirement: The development email sender cannot run outside development

Replaced by the requirement below rather than edited in place. The old requirement's title already
said "outside development" while its scenarios permitted staging — the backfill recorded the code
faithfully in the scenarios and let the title overstate it. Making the two agree restructures the
scenario set, so the requirement is removed and re-stated.

## ADDED Requirements

### Requirement: The development email sender runs only in development

The framework SHALL offer an email sender that does nothing, for local development, and it SHALL
refuse to be registered in any environment other than development.

Silently dropping a confirmation or password-reset email is not a degraded mode. It removes the only
way an account's address can be confirmed, and an unconfirmed address blocks external-login
auto-linking — so the failure surfaces as "external sign-in does not link", several steps from its
cause.

#### Scenario: The host is in development

- **WHEN** the no-op sender is registered in development
- **THEN** registration succeeds

#### Scenario: The host is in production

- **WHEN** it is registered in production
- **THEN** registration fails, naming the current environment and directing the caller to a real
  sender

#### Scenario: The host is in staging or an unrecognised environment

- **WHEN** it is registered in staging, or in any environment name the framework does not recognise
- **THEN** registration fails the same way — the guard admits one named environment rather than
  refusing one, because a name it has never heard of is not evidence that the host is safe

#### Scenario: A host outside development wants mail dropped

- **WHEN** a host genuinely wants outbound mail dropped outside development
- **THEN** it registers its own sender — the framework ships none for that case, because a shipped
  no-op is indistinguishable at every call site from a working one
