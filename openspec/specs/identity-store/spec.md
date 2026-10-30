# identity-store Specification

## Purpose
One identity store — accounts, their credentials and roles, and the tenant directory — that several
hosts built on the framework can share, created and evolved by migrations the framework ships, so
that a person has one account across every host that uses it.

## Requirements
### Requirement: The framework ships the identity store together with its migrations

The framework SHALL offer a ready identity store on PostgreSQL holding the accounts and everything an
account carries — passwords, authenticator keys, recovery codes, passkeys, external logins, claims,
tokens and roles — and the tenant directory: memberships, tenant selections, settings and API keys.
It SHALL ship the schema migrations that create the store and evolve it, so that a host using the
store generates no migrations of its own for these tables.

Registering the store SHALL make the directory's stores available on it without a further
registration, and the framework's identity registration SHALL accept the store as the one its
accounts and roles live in, so that a host cannot end up with accounts in the shared store and
memberships somewhere else.

#### Scenario: A host migrates an empty database

- **WHEN** a host registers the identity store and applies its migrations to a database that holds no
  identity tables
- **THEN** every table of the store is created, and an account can be created, signed in with a
  password and a passkey, given a membership, a selection, a setting and an API key — verified on
  PostgreSQL

### Requirement: The store lives in one fixed schema, at a configurable location

The store's tables and its migration history SHALL live in one schema whose name is fixed, the same
for every host and every release, and stated in the documentation. The history SHALL be kept inside
that schema, apart from any history a host keeps for its own contexts.

The database the store lives in SHALL be configurable on its own, through a connection of its own
that falls back to the host's default connection where none is given, so that hosts which keep their
own databases can share one identity database. Neither SHALL be routed by tenant: the identity store
is one database for every tenant.

#### Scenario: The store is migrated

- **WHEN** the identity store's migrations are applied
- **THEN** its tables and its migration history are in the fixed schema, and no other schema of the
  database is touched

#### Scenario: Hosts keep their own databases

- **WHEN** two hosts whose own stores live in different databases both give the identity store the
  same connection
- **THEN** both read and write the same accounts and memberships

#### Scenario: No connection of its own is given

- **WHEN** a host gives the identity store no connection of its own
- **THEN** the store uses the host's default connection

### Requirement: Every host applies the same migrations, and never two at once

Every host that uses the store SHALL apply the same shipped migrations, identified the same way, so
that a migration one host applied is recognised as applied by every other. Where several hosts apply
migrations to the same store at the same time, each migration SHALL be applied once, and a host SHALL
wait for another that is migrating rather than fail or apply it a second time.

#### Scenario: A second host starts on a migrated store

- **WHEN** one host has migrated the store and a second host, built on the same release, then applies
  the store's migrations
- **THEN** it applies nothing, fails nothing, and uses the store

#### Scenario: Two hosts migrate at once

- **WHEN** two hosts apply the store's migrations to an unmigrated store at the same time
- **THEN** both complete, every table exists once, and each migration is recorded once — verified on
  PostgreSQL

### Requirement: The account shape is the framework's

The shared store's account type SHALL be the standard account type of ASP.NET Identity; the store
SHALL offer no way to extend it with columns of a host's own. Data a host keeps about a person beyond
the account belongs outside the identity store.

A host whose identity configuration would write account columns the other hosts cannot read or match
— protection of personal data in the store — SHALL be refused at start.

Hosting the identity tables in a host's own context, with a user type of its own, SHALL remain
possible for a host that does not share its store.

#### Scenario: A host registers the shared store

- **WHEN** a host registers the shared identity store
- **THEN** it registers it without naming a user type, and its accounts are of the standard type

#### Scenario: A host protects personal data in the store

- **WHEN** a host that registers the shared identity store turns on the protection of personal data
- **THEN** the host fails to start, naming the option

### Requirement: The schema changes only additively within a major version

Within one major version, a release SHALL change the store's schema only additively: a new table, a
new index, or a new column that is nullable or has a database default. No release within the major
version SHALL rename, drop or retype what an earlier one created. A change that is not additive SHALL
be made only in a major version and listed as a breaking change.

A host on an earlier release of the same major version SHALL keep working on a store that a later
release migrated, and SHALL NOT fail because the store records migrations it does not know.

#### Scenario: Hosts on two releases share the store

- **WHEN** a host on a later release has migrated the store and a host on an earlier release of the
  same major version starts on it and applies its own migrations
- **THEN** the earlier host applies nothing, fails nothing, and signs users in, reads memberships and
  writes accounts as before — verified on PostgreSQL

### Requirement: Moving onto the shared store replaces the old store

The framework SHALL NOT convert an identity store a host kept in its own context into the shared one.
The documentation SHALL state how a host moves: remove the old store's tables and its migration
history, register the shared store and apply its migrations, and seed it. It SHALL state that the seed
has to recreate users and tenants with the identifiers they had, because event streams record their
owners by those identifiers and encrypted data is bound to them, and that whatever the seed does not
recreate — password hashes, authenticator keys, passkeys, API keys and encrypted settings — is lost.

#### Scenario: A host reads how to move

- **WHEN** a host that keeps its identity tables in its own context reads the shared store's guide
- **THEN** it finds the steps, the requirement to keep user and tenant identifiers, and what is lost
