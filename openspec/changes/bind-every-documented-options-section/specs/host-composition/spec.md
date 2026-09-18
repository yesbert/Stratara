# host-composition

## ADDED Requirements

### Requirement: A setting that names its configuration section is read from it

Every framework setting whose documentation names a configuration section SHALL be read from that
section of the host's configuration by the registration that adds it, whether the host calls that
registration directly or through a composite, so that a value written in the named section takes
effect without code. Where the host's services carry no configuration, the registration SHALL still
succeed with the defaults. A value the host configures in code after the registration SHALL take
precedence over the section. A setting whose value cannot work SHALL be refused when the host starts,
with a message naming the setting, rather than accepted and failing later; the projection replay's
lease period SHALL be refused at zero or below.

#### Scenario: The session context is configured in the application settings

- **WHEN** a host sets the tenant-header opt-in in the session-context section of its configuration
  and registers the session context
- **THEN** the opt-in is in effect, without a line of code configuring it

#### Scenario: The replay lease is configured in the application settings

- **WHEN** a host sets the replay lease in the projection-replay section of its configuration
- **THEN** a replay's marking lasts that long

#### Scenario: Code overrides the section

- **WHEN** a host sets a value in the section and configures the same setting in code after
  registering
- **THEN** the value from code is in effect

#### Scenario: The replay lease is zero

- **WHEN** a host configures a replay lease of zero seconds
- **THEN** the host fails to start with a message naming the setting

#### Scenario: No configuration is registered

- **WHEN** the session context or the replay state is registered on a service collection that carries
  no configuration
- **THEN** the registration succeeds and the defaults apply
