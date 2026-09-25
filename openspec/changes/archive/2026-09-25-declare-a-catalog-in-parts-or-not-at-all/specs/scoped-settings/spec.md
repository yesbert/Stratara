## MODIFIED Requirements

### Requirement: The setting vocabulary is declared in code and is strict

An application SHALL declare each setting once, with its name, its code default, whether it
inherits and whether it is encrypted. Declaring the same name twice SHALL fail, and reading a name
that was never declared SHALL fail.

A setting nobody declared is a typo, and returning nothing for it would make the typo look like an
unset value.

The vocabulary MAY be declared in several parts, for example one per module. The parts together
SHALL form one vocabulary, whatever order they are registered in and whether they are registered
before or after the setting store. A later part SHALL NOT replace an earlier one, and a name
declared in two parts SHALL fail as a name declared twice in one part does. A vocabulary that was
registered in a way the framework cannot add to SHALL make the next declaration fail at
registration, naming the reason, rather than be replaced or ignored.

An application that declares no settings SHALL still be able to register the setting store. The
store SHALL resolve and work, the removal of a subject's settings among them, and reading any
setting SHALL fail as undeclared. A missing vocabulary SHALL NOT surface only when the store is
first used.

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

#### Scenario: The vocabulary is declared in two parts

- **WHEN** two parts each declare different settings, one registered before the setting store and
  one after it
- **THEN** every setting of both parts can be read

#### Scenario: Two parts declare the same name

- **WHEN** a second part declares a name the first part already declared
- **THEN** declaration fails, naming the setting

#### Scenario: The store is registered and no setting is declared

- **WHEN** a host registers the setting store and declares no settings
- **THEN** the store resolves, removing a subject's settings succeeds, and reading any setting name
  fails as undeclared

#### Scenario: The vocabulary was registered as a factory

- **WHEN** a host registers the vocabulary through a factory of its own and then declares a part
- **THEN** the declaration fails at registration, saying that the registered vocabulary cannot be
  added to
