## MODIFIED Requirements

### Requirement: Telemetry export is configured by environment, not by code

Where an OpenTelemetry endpoint is configured in the environment, the framework SHALL export traces,
metrics and logs to it. Where none is configured, the framework SHALL configure telemetry without an
exporter rather than failing.

`ConfigureOpenTelemetry()` and `ConfigureAspNetOpenTelemetry()` MAY both be called, in any order and any
number of times, and SHALL configure telemetry once, while applying the callbacks of every call. Where
an endpoint is configured and no export timeout is, the export timeout SHALL default to 5000 ms, set in
the host's configuration and without changing the process environment.

#### Scenario: An export endpoint is configured

- **WHEN** an OpenTelemetry endpoint is present in configuration
- **THEN** traces, metrics and logs are exported to it, carrying the configured service name

#### Scenario: No export endpoint is configured

- **WHEN** no OpenTelemetry endpoint is configured
- **THEN** telemetry is still collected in-process and no exporter is registered — a host runs
  locally without an observability backend

#### Scenario: Both configuration methods are called

- **WHEN** a host with an OpenTelemetry endpoint calls both methods, in either order and more than once
- **THEN** the host starts, and request instrumentation is added once

#### Scenario: No export timeout is configured

- **WHEN** an OpenTelemetry endpoint is configured and no export timeout is present in the host's
  configuration, which includes the process environment
- **THEN** the exporter waits 5000 ms, and the process environment is unchanged

#### Scenario: An export timeout is configured

- **WHEN** an export timeout is present in the host's configuration or the process environment
- **THEN** that timeout is the one in force
