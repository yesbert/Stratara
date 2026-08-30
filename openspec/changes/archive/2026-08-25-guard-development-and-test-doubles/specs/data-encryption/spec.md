## MODIFIED Requirements

### Requirement: The development key store cannot run outside development

The framework SHALL ship a key store for local development that uses a fixed, publicly known key,
and it SHALL refuse to be constructed in any environment other than development. Where it is in
use at all, the host SHALL emit a warning at start-up.

The guard is a whitelist, not a blacklist: an environment named anything other than development —
staging, QA, UAT, preview, or a name the framework has never heard of — is refused.

The development store SHALL NOT simulate an operation it does not perform. In particular it SHALL
NOT report a successful erasure without shredding anything.

#### Scenario: The host runs in development

- **WHEN** the development key store is constructed in the development environment
- **THEN** it works, and the host emits a start-up warning that it is active

#### Scenario: The host runs anywhere else

- **WHEN** the development key store is constructed in any environment that is not development
- **THEN** construction fails, naming the current environment and how to register a real key store

#### Scenario: A real key store is registered

- **WHEN** a real key store is registered before the security composition
- **THEN** that store is used, the development fallback is never constructed, and no warning is
  emitted

#### Scenario: An erasure is attempted against the development store

- **WHEN** a revocation or a scope erasure is attempted against the development key store
- **THEN** it fails rather than reporting success — a development stub must not make an erasure path
  look exercised when nothing was shredded
