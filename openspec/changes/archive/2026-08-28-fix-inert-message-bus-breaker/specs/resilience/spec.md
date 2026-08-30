## MODIFIED Requirements

### Requirement: Message-bus traffic retries indefinitely behind a circuit breaker

The message-bus policy SHALL retry indefinitely with exponential, jittered backoff bounded by a
maximum delay, behind a circuit breaker. The circuit SHALL open under sustained failure and SHALL
close again once the broker recovers.

A broker outage is expected to end, so giving up would discard work that will succeed shortly. The
circuit breaker is what makes a *permanent* failure distinguishable from a passing one: without it,
an outage of ten minutes and an outage of ten seconds look the same to an operator, differing only
in how long the retries continue.

The breaker's counting window SHALL be wide enough for the retry's own maximum delay to fill it. A
window narrower than the delay admits fewer failures than the breaker requires, which leaves the
breaker unable to open at all — stated here because that state is invisible: nothing fails, no test
breaks, and the only symptom is an alert that never fires.

#### Scenario: The broker is unavailable and recovers

- **WHEN** message-bus operations fail while the broker is down and the broker later recovers
- **THEN** the operation eventually succeeds without the caller having implemented any retry

#### Scenario: The broker stays unavailable

- **WHEN** message-bus operations fail continuously for longer than the breaker's counting window
- **THEN** the circuit opens, and that is observable to the host

#### Scenario: The circuit is open and the broker returns

- **WHEN** the broker recovers while the circuit is open
- **THEN** the circuit closes again and traffic resumes, without the caller intervening

#### Scenario: The retry's maximum delay is changed

- **WHEN** the retry's maximum delay is raised so that fewer failures fall inside the breaker's
  counting window than the breaker requires to open
- **THEN** that is a defect the framework's own tests detect, rather than a silently inert breaker
