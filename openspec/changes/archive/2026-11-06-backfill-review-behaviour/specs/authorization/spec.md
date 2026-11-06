## MODIFIED Requirements

### Requirement: Catalog permissions are usable as HTTP authorization policies

Every permission declared in the catalog SHALL be usable as an HTTP authorization policy name
without registering it individually. A policy name that is not a declared permission SHALL be left
to the host's own policy resolution.

`AddStrataraPermissionPolicies()` SHALL do this whether the framework's default policy provider was
registered before or after it, and SHALL still serve the host's named, default and fallback policies. A
policy provider of the host's own SHALL be kept instead, and composing the permission lookup into it is
then the host's: without that, a declared permission is not usable as a policy name.

#### Scenario: An endpoint is guarded by a declared permission name

- **WHEN** an endpoint requires an authorization policy whose name is a declared permission
- **THEN** the request is permitted only if the caller's effective permissions include it

#### Scenario: An endpoint is guarded by a name that is not a permission

- **WHEN** an endpoint requires a policy name that is not declared in the catalog
- **THEN** resolution falls through to the host's own policy provider unchanged

#### Scenario: The HTTP path has no tenant

- **WHEN** a permission policy is evaluated for a principal carrying no tenant claim
- **THEN** the request is refused rather than evaluated against an unscoped permission set

#### Scenario: The host registers authorization before or after the policies

- **WHEN** `AddAuthorization()`, `AddAuthorizationCore()` or `AddControllers()` runs before or after
  `AddStrataraPermissionPolicies()`
- **THEN** a declared permission resolves as a policy, and a fallback policy the host configures is
  still served

#### Scenario: The host supplies its own policy provider

- **WHEN** a host registers a policy provider of its own and calls `AddStrataraPermissionPolicies()`
- **THEN** the host's provider is the one resolved
