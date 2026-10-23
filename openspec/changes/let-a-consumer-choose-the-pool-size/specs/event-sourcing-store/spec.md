## ADDED Requirements

### Requirement: The connection pool size is the consumer's to choose

Where a host registers a PostgreSQL context through the framework's registrations, a maximum pool size the host's
connection string names SHALL be the maximum pool size the context's connections use, whatever its value. Where the
connection string names none, the registration SHALL apply the framework's documented default. When a context's
connection string is first used, the host SHALL log the maximum pool size in effect and whether it came from the
connection string or from the default. The documentation SHALL state the default, why the framework chose it, and
that contexts that share a connection string share its pool.

#### Scenario: The connection string names the provider's own default

- **WHEN** a host registers a context with a connection string that names a maximum pool size of 100
- **THEN** the context's connections use a pool of at most 100, and the start log names 100 as the connection
  string's own

#### Scenario: The connection string names no pool size

- **WHEN** a host registers a context with a connection string that names no maximum pool size
- **THEN** the context's connections use the documented default, and the start log names it as the framework's
  default

#### Scenario: The connection string names another pool size

- **WHEN** a host registers a context with a connection string that names a maximum pool size of 40
- **THEN** the context's connections use a pool of at most 40
