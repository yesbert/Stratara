## MODIFIED Requirements

### Requirement: The directory tables can be hosted in an existing context

A consumer SHALL be able to add the directory's tables to a database context it already has, so the
directory does not force a second migration lineage.

The consumer SHALL be able to choose how the directory's stores obtain that context: shared for the
whole request, or a fresh one for each operation. The framework SHALL state, where each choice is
declared, what the choice costs — that a shared context permits only one operation at a time and
that a store's write on it also commits whatever the consumer has left unsaved on that context, and
that a context per operation has neither property but places a store's write outside any transaction
the consumer opened on their own context.

Sharing the request's context remains the default, so a consumer who chooses nothing keeps the
behaviour they have.

#### Scenario: A consumer hosts the directory in its own context

- **WHEN** a consumer applies the directory's model to an existing context
- **THEN** the directory tables are part of that context's model and migrations

#### Scenario: Directory work is issued concurrently within one request

- **WHEN** a consumer has chosen a context per operation and issues two directory operations at the
  same time within one request
- **THEN** both complete, rather than one failing because the other holds the context

#### Scenario: A consumer changes nothing

- **WHEN** a consumer registers the directory's stores as before
- **THEN** the stores share the request's context exactly as they did, and the constraint that
  follows from sharing it is stated where that registration is declared
