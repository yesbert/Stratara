# scoped-settings Specification

## Purpose
Let one named setting have a different value per user, per tenant and globally, resolved in a
predictable order — so an application asks for a value once and gets the most specific one that
applies to whoever is asking.

## Requirements

### Requirement: The setting vocabulary is declared in code and is strict

An application SHALL declare each setting once, with its name, its code default, whether it
inherits and whether it is encrypted. Declaring the same name twice SHALL fail, and reading a name
that was never declared SHALL fail.

A setting nobody declared is a typo, and returning nothing for it would make the typo look like an
unset value.

#### Scenario: A setting is declared

- **WHEN** a setting is declared
- **THEN** it appears in the catalogue and can be read

#### Scenario: A name is declared twice

- **WHEN** the same name is declared a second time
- **THEN** declaration fails, naming the setting

#### Scenario: An undeclared name is read

- **WHEN** a name that was never declared is read
- **THEN** the read fails rather than returning nothing

#### Scenario: A name is empty

- **WHEN** a declaration carries an empty name
- **THEN** it is rejected

### Requirement: Storage is per exact scope

The store SHALL hold one value per name per exact scope, and SHALL return only what was written at
the scope asked for. It SHALL NOT resolve or inherit — resolution is a separate concern layered on
top.

#### Scenario: A value is written and read at one scope

- **WHEN** a value is written at a scope and read back at that same scope
- **THEN** it is returned

#### Scenario: A value is read at a different scope

- **WHEN** a value written at one scope is read at a different one
- **THEN** nothing is returned, even where the scopes are related

#### Scenario: A value is overwritten

- **WHEN** a value is written twice at the same scope
- **THEN** the second replaces the first

#### Scenario: A value is cleared

- **WHEN** a value is written as absent
- **THEN** the stored value is removed rather than stored as an empty one

#### Scenario: Every value at a scope is listed

- **WHEN** all values at a scope are requested
- **THEN** only values written at exactly that scope are returned

### Requirement: Reading resolves from the most specific scope outward

Reading SHALL try, in order: the current user within the current tenant, then the user, then the
tenant, then global, then the host's configuration, then the setting's declared default. The first
of these that has a value wins.

#### Scenario: Several scopes have a value

- **WHEN** a setting has values at more than one scope
- **THEN** the most specific one that applies to the current session is returned

#### Scenario: Only an outer scope has a value

- **WHEN** no specific scope has a value but an outer one does
- **THEN** the outer value is returned

#### Scenario: Nothing is stored anywhere

- **WHEN** no scope has a value but the host's configuration provides one
- **THEN** the configured value is returned

#### Scenario: Not even configuration has it

- **WHEN** neither storage nor configuration has a value
- **THEN** the declared code default is returned

#### Scenario: Storage disagrees with configuration

- **WHEN** a value exists in storage and also in the host's configuration
- **THEN** the stored value wins — configuration is a fallback, not an override

### Requirement: A setting can opt out of inheritance

A setting declared as not inherited SHALL be resolved only from the most specific scope, falling
through to configuration and the code default without consulting the outer scopes.

#### Scenario: A non-inherited setting has no value at the specific scope

- **WHEN** a non-inherited setting is read and the most specific scope has no value
- **THEN** the outer scopes are not consulted, and resolution falls through to configuration and the
  default

### Requirement: Resolution is driven by the session's subject

The scopes resolution walks SHALL be derived from the current session's data-owner tenant and user.
Where no session is set, only the global scope SHALL apply.

#### Scenario: A session is set

- **WHEN** a setting is read within a session
- **THEN** the user and tenant scopes tried are the session's data owner

#### Scenario: No session is set

- **WHEN** a setting is read with no session
- **THEN** only the global scope, configuration and the default are consulted

### Requirement: Values can be read as their declared type

Reading SHALL offer a typed form that converts the resolved value, falling back to a supplied default
where nothing resolves.

#### Scenario: A stored value is read as a type

- **WHEN** a stored value is read in typed form
- **THEN** it is converted to the requested type

#### Scenario: Nothing resolves

- **WHEN** nothing resolves for a typed read
- **THEN** the supplied fallback is returned

### Requirement: A setting declared encrypted is encrypted, or the read fails

A setting declared encrypted SHALL be stored as ciphertext and decrypted transparently on read. If
no encryptor is available, the operation SHALL fail rather than storing or returning plaintext.

Silent plaintext is the one outcome that must not be possible: it would be invisible at the call
site and discovered by whoever reads the database.

#### Scenario: An encrypted setting round-trips

- **WHEN** a setting declared encrypted is written and read back
- **THEN** the value returned is the one written, and what is stored is ciphertext

#### Scenario: A plaintext setting is written alongside

- **WHEN** a setting not declared encrypted is written
- **THEN** it passes through untouched, and listing a scope decrypts only the encrypted ones

#### Scenario: No encryptor is registered

- **WHEN** an encrypted setting is used with no encryptor available
- **THEN** the operation fails loudly rather than falling back to plaintext

#### Scenario: An encrypted setting is cleared

- **WHEN** an encrypted setting is written as absent
- **THEN** it is deleted without the encryptor being involved

### Requirement: Encrypted settings are bound to their scope and their name

Encryption SHALL be bound to the setting's scope and to the setting's own name, so a stored value
cannot be decrypted as a different setting or under a different scope, and so destroying a scope's
key destroys its settings with it.

#### Scenario: A stored value is moved to another scope

- **WHEN** an encrypted value is decrypted under a different scope than it was written for
- **THEN** decryption fails

#### Scenario: A subject's key is destroyed

- **WHEN** a scope's key material is destroyed
- **THEN** that scope's encrypted settings become unrecoverable, without the rows having to be found
  and deleted

### Requirement: Erasure sweeps remove a subject's settings across scopes

Deleting a user scope SHALL remove that user's settings across every tenant. Deleting a tenant scope
SHALL remove that tenant's settings across every user. Deleting the global scope SHALL remove only
global settings.

#### Scenario: A user is erased

- **WHEN** a user scope is deleted
- **THEN** that user's settings are removed in every tenant, and other users' are untouched

#### Scenario: A tenant is removed

- **WHEN** a tenant scope is deleted
- **THEN** that tenant's settings are removed for every user

#### Scenario: The global scope is deleted

- **WHEN** the global scope is deleted
- **THEN** only global settings are removed — a global delete is not a delete-everything
