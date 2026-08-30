# external-identity Specification

## Purpose
Let a user sign in with a local password or with an external identity provider, and have a local
account exist afterwards — without an unverified email address at any provider being enough to take
over someone's account.

## Requirements

### Requirement: An external login is identified by the provider's subject, never by email

An external login SHALL be linked to a local account by the pair of provider and provider-issued
subject identifier. An email address SHALL NOT by itself establish that a login belongs to an
account.

An email address is an attribute a provider asserts; the subject is the identity it issues. Treating
the attribute as the identity is what makes an account takeover possible from any provider that
lets a user set an unverified address.

#### Scenario: A login is already linked

- **WHEN** a login arrives whose provider and subject are already linked to a local account
- **THEN** that account is signed in, without any email being consulted

#### Scenario: The same email arrives from a different subject

- **WHEN** a login arrives carrying an email that matches a local account, but a subject that is not
  linked to it
- **THEN** the match alone does not sign the user in

### Requirement: Auto-linking to an existing account requires verification on both sides

Where an incoming login's email matches an existing local account, linking SHALL happen
automatically only if the provider asserts the address is verified **and** the local account's
address is confirmed. Otherwise the outcome SHALL be that interactive linking is required.

The framework SHALL NOT silently merge in any other case.

#### Scenario: Both sides verified

- **WHEN** the provider asserts a verified email and the matching local account has a confirmed
  address
- **THEN** the external login is linked to that account and the user is signed in

#### Scenario: The provider did not verify

- **WHEN** the provider does not assert the address as verified
- **THEN** the result is that interactive linking is required — the accounts are not merged

#### Scenario: The local account is unconfirmed

- **WHEN** the provider asserts a verified address but the local account's address is not confirmed
- **THEN** the result is still that interactive linking is required

#### Scenario: A host accepts the risk explicitly

- **WHEN** a host turns off the verification requirement
- **THEN** linking proceeds on an email match alone — the framework's default is the safe one and
  the unsafe behaviour must be chosen

### Requirement: More than one claim can assert that an address is verified

The framework SHALL treat any of a configurable set of claims as the provider's verification signal,
defaulting to the standard claim and to the vendor-specific claim that some providers issue instead.

#### Scenario: A provider issues a vendor-specific verification claim

- **WHEN** the provider asserts verification through a vendor-specific claim rather than the standard
  one
- **THEN** it is honoured

### Requirement: Provisioning a new account is opt-out and requires an email

Where no local account matches, the framework SHALL create one and link the login to it. A host
SHALL be able to disable that. Provisioning SHALL require an email address.

The provisioned account's address SHALL be marked confirmed only if the provider asserted it as
verified.

#### Scenario: A first-time user signs in

- **WHEN** a login arrives with no linked account and no matching email, and provisioning is enabled
- **THEN** an account is created, the login is linked to it, and its address is marked confirmed
  only if the provider verified it

#### Scenario: Provisioning is disabled

- **WHEN** provisioning is disabled and no account matches
- **THEN** the sign-in is denied and nothing is created

#### Scenario: The login carries no email

- **WHEN** a login arrives with no email and no matching account
- **THEN** the sign-in is denied rather than an account being created without one

### Requirement: A host can gate who is allowed in at all

The framework SHALL let a host supply a gate that runs before any account is created or linked,
receiving the provider, the subject, the resolved email and whether the provider verified it.
Rejection SHALL deny the sign-in and create nothing.

#### Scenario: The gate accepts

- **WHEN** the gate accepts, provisioning proceeds

#### Scenario: The gate rejects

- **WHEN** the gate rejects
- **THEN** the sign-in is denied, and no account is created and no login is linked

#### Scenario: The gate inspects what it was given

- **WHEN** the gate runs
- **THEN** it receives the provider, the subject, the resolved email and the provider's verification
  signal, so an invitation list can be checked against a verified address

### Requirement: Every outcome is distinguishable and every refusal is recorded

Provisioning SHALL report which of the possible outcomes occurred — signed in existing, linked,
provisioned, interactive linking required, or denied — and SHALL record a reason for each refusal.

The caller owns the user experience: the framework decides what is safe and reports it, and does not
redirect, render or sign anything in.

#### Scenario: A sign-in is denied

- **WHEN** any refusal occurs
- **THEN** the outcome identifies it, a reason is available to the caller, and the refusal is
  recorded with its provider and reason

#### Scenario: The consumer builds its own callback

- **WHEN** a consumer handles an external-login callback
- **THEN** it calls the framework's provisioning and decides what to do with the outcome — the
  framework ships no endpoint and no page

### Requirement: External providers are registered as ordinary schemes from configuration

The framework SHALL offer configuration-driven registration of an OpenID Connect scheme and a bearer
token scheme, so that a consumer wires an external provider with one call and a configuration
section rather than by hand.

#### Scenario: An OpenID Connect provider is configured

- **WHEN** a host registers the OpenID Connect scheme from configuration
- **THEN** the authority, client credentials and scopes are read from the configured section

#### Scenario: No scopes are configured

- **WHEN** no scopes are configured
- **THEN** a default set is applied rather than none

#### Scenario: Scopes are configured

- **WHEN** scopes are configured
- **THEN** they replace the defaults rather than adding to them

#### Scenario: An API accepts tokens from several issuers

- **WHEN** a host configures more than one valid issuer for the bearer scheme
- **THEN** tokens from each configured issuer are accepted, and tokens from others are not

### Requirement: The provider's subject remains the identifier after claim mapping

Scheme registration SHALL ensure that the identifier the provisioning path reads is the provider's
subject claim, regardless of the framework's default claim renaming.

Default claim mapping renames incoming claims, and a mapping that loses the subject would silently
turn identity into whatever survived — which is how linking falls back to email.

#### Scenario: An OpenID Connect principal arrives

- **WHEN** a principal arrives from the OpenID Connect scheme
- **THEN** the provider's subject claim is what the provisioning path uses as the provider key

#### Scenario: A bearer token arrives

- **WHEN** a bearer token is validated
- **THEN** claim renaming is disabled, and the subject and role claims are read under their
  protocol names

### Requirement: Sign-in is exposed through a channel-agnostic contract

The framework SHALL expose sign-in, sign-out and two-factor operations through a contract that does
not depend on the hosting channel, returning a result that distinguishes success, invalid
credentials, lockout, not-allowed and two-factor-required.

A failed sign-in SHALL NOT reveal whether the account exists: invalid credentials and
account-not-allowed SHALL be reported with the same message.

#### Scenario: Credentials are correct

- **WHEN** correct credentials are supplied
- **THEN** the result is success and carries the user's identity

#### Scenario: Credentials are wrong, or the account is not allowed to sign in

- **WHEN** either occurs
- **THEN** the result carries the same message in both cases, so the response does not disclose
  whether the account exists

#### Scenario: The account is locked out

- **WHEN** the account is locked out
- **THEN** the result says so — a lockout is already known to the user and hiding it prevents them
  from waiting it out

#### Scenario: Two factors are required

- **WHEN** a second factor is required
- **THEN** the result says so without a message, and the second-factor and recovery-code paths
  report their own failures distinctly

### Requirement: Identity messages are localisable

The framework's own identity messages SHALL be localisable, falling back to the default language for
a culture it has no resources for.

#### Scenario: A supported culture is active

- **WHEN** a culture the framework has resources for is active
- **THEN** messages are returned in that language

#### Scenario: An unsupported culture is active

- **WHEN** a culture with no resources is active
- **THEN** messages fall back to the default language rather than returning a key

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

