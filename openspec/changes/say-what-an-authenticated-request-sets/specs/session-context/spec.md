## MODIFIED Requirements

### Requirement: An authenticated HTTP request populates the context from its claims

For an HTTP request carrying an authenticated principal, the framework SHALL populate the session
context from that principal's claims before the request reaches application code. The actor tenant
and the data-owner tenant SHALL be the same tenant, and the claimed user SHALL be the actor user. The
data-owner user SHALL be left absent unless the application sets it.

The data-owner user is left absent on purpose: the scope under which a protected field is encrypted
follows the data owner, so a framework that filled in the data-owner user for every request would
move every protected field written from a request to a per-user scope, and data already written
under the tenant scope would no longer be readable in the same way.

#### Scenario: An authenticated request arrives

- **WHEN** a request arrives with an authenticated principal
- **THEN** the session context is populated for that request, with the actor user taken from the
  principal's name-identifier claim and the tenant taken from the `stratara:tenant_id` claim
- **AND** the actor tenant and the data-owner tenant are that same tenant
- **AND** the data-owner user is absent

#### Scenario: An unauthenticated request arrives

- **WHEN** a request arrives whose principal is not authenticated
- **THEN** no session context is set for it, and the request proceeds

#### Scenario: The principal carries no name-identifier claim

- **WHEN** an authenticated principal has no name-identifier claim, or one that is not a parsable
  identifier
- **THEN** the user identity is the empty identifier rather than the request being rejected
