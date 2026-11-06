## ADDED Requirements

### Requirement: An encrypted setting whose key is gone reads as absent

Reading an encrypted setting whose key was revoked or erased SHALL yield an absent value and record a
warning naming the setting, and the rest of the scope SHALL still read. A stored value that fails
authentication or is not ciphertext, or whose key cannot be resolved for another reason, SHALL fail the
read rather than read as absent.

An erasure leaves such rows behind, so one destroyed key must not make its scope unreadable.

#### Scenario: One setting's key was destroyed

- **WHEN** a setting's key was revoked or its scope erased, and that setting is read
- **THEN** it reads as absent and the warning names the setting, the tenant and the user

#### Scenario: A scope holding such a setting is listed

- **WHEN** every value at a scope is requested and one setting's key is gone
- **THEN** the other values are returned and that setting is left out

#### Scenario: An inherited setting's key was destroyed

- **WHEN** resolution reaches a scope whose stored value has lost its key
- **THEN** resolution goes on as it does for a scope with no value

#### Scenario: A stored value was altered

- **WHEN** the ciphertext or the authentication tag of a stored value was altered
- **THEN** the read fails

#### Scenario: The key store fails for another reason

- **WHEN** resolving the key fails for a reason other than the key being gone
- **THEN** the read fails rather than reading as absent

#### Scenario: A stored value was moved

- **WHEN** a stored value was copied to another setting's row or another scope, and its key still exists
- **THEN** the read fails

#### Scenario: A plaintext value is stored for an encrypted setting

- **WHEN** a row of a setting declared encrypted holds a value that is not ciphertext
- **THEN** the read fails

#### Scenario: A read is cancelled

- **WHEN** reading an encrypted value is cancelled
- **THEN** the cancellation reaches the caller; the value does not read as absent
