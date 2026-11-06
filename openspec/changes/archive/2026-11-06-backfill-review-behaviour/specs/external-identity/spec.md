## MODIFIED Requirements

### Requirement: The provider's subject remains the identifier after claim mapping

Scheme registration SHALL ensure that the identifier the provisioning path reads is the provider's
subject claim, regardless of the framework's default claim renaming.

Default claim mapping renames incoming claims, and a mapping that loses the subject would silently
turn identity into whatever survived — which is how linking falls back to email.

A principal validated through `AddStrataraJwtBearer` SHALL also carry its subject as the name identifier,
unless the token already carries one, because that is the claim the session, the permission policies
and the tenant claim read. A token-validated handler the host sets on the scheme's events SHALL still
run, and SHALL see that claim.

#### Scenario: An OpenID Connect principal arrives

- **WHEN** a principal arrives from the OpenID Connect scheme
- **THEN** the provider's subject claim is what the provisioning path uses as the provider key

#### Scenario: A bearer token arrives

- **WHEN** a bearer token is validated
- **THEN** claim renaming is disabled, and the subject and role claims are read under their
  protocol names

#### Scenario: A validated bearer principal is built

- **WHEN** a bearer token without a name-identifier claim is validated
- **THEN** the principal carries the subject under its protocol name and as the name identifier

#### Scenario: The token carries a name identifier of its own

- **WHEN** a validated bearer token already carries a name-identifier claim
- **THEN** that claim is kept and no second one is added

#### Scenario: The host configures a token-validated handler

- **WHEN** a host sets a token-validated handler on the scheme's events, before or after registering
  the scheme
- **THEN** the handler runs, with the name identifier already on the principal

#### Scenario: The host replaces the scheme's event handling

- **WHEN** a host resolves the scheme's events through an events type, or sets a subclass that overrides
  the token-validated step without calling its base
- **THEN** the name identifier is not added, and the session's actor is the empty identifier
