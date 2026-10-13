# mediator-dispatch Specification

## Purpose
Give an application one way in — a request goes to exactly one handler, through a pipeline of
cross-cutting stages, with no caller ever holding a handler reference — so that guards, auditing and
retries apply to every call by construction rather than by discipline.

## Requirements

### Requirement: Requests are commands or queries, with or without a result

The framework SHALL distinguish commands, which change state, from queries, which do not, and SHALL
support both with and without a result. A query SHALL NOT have side effects.

#### Scenario: A command changes state

- **WHEN** an operation changes state
- **THEN** it is expressed as a command, and the framework can distinguish it from a query at
  dispatch time without inspecting what the handler does

#### Scenario: A query is re-issued

- **WHEN** a caller issues the same query twice
- **THEN** nothing in the system has changed as a result — this is a contract the framework relies
  on, not something it enforces

### Requirement: A request reaches exactly one handler

Dispatching SHALL resolve one handler for the request's type and invoke it. Where no handler is
registered, the framework SHALL fail with a message naming the request type, rather than silently
doing nothing.

#### Scenario: A handler is registered

- **WHEN** a request whose type has a registered handler is dispatched
- **THEN** that handler is invoked, and its result — if the request has one — is returned to the
  caller unchanged

#### Scenario: No handler is registered

- **WHEN** a request whose type has no registered handler is dispatched
- **THEN** dispatch fails with a message naming the request type

#### Scenario: A null request is dispatched

- **WHEN** a caller dispatches nothing
- **THEN** the call is rejected immediately rather than failing later inside the pipeline

### Requirement: Pipeline stages nest in registration order

Cross-cutting stages SHALL wrap the handler in the order they were registered: the first registered
stage is the outermost and runs first, the last registered is nearest the handler.

This is the mechanism every other capability's ordering guidance rests on — validation "first",
tenant isolation "after validation", resilience "inside the guards" — and it is stated once here
rather than restated by each.

#### Scenario: Several stages are registered

- **WHEN** stages are registered in a given order and a request is dispatched
- **THEN** each stage runs before the ones registered after it, and each has the opportunity to
  short-circuit the ones inside it

#### Scenario: A stage short-circuits

- **WHEN** a stage fails the request rather than continuing
- **THEN** neither the stages registered after it nor the handler run

#### Scenario: A request type has no stages registered

- **WHEN** no stages are registered
- **THEN** the handler is invoked directly

### Requirement: A stage is registered as an open generic and applies to every request

A cross-cutting stage SHALL be registered as an open generic type covering either request shape, and
SHALL be closed over each request type as it is dispatched. Registering a type that is not an open
generic of the right arity SHALL be rejected at registration time.

Registering the same stage more than once SHALL install it once. A host that composes overlapping
bundles of framework services must not silently run a stage twice.

#### Scenario: A valid stage is registered

- **WHEN** an open generic stage of the correct arity is registered
- **THEN** it resolves for every request of the matching shape

#### Scenario: An invalid stage is registered

- **WHEN** the registered type is absent, is not generic, or has the wrong number of type parameters
- **THEN** registration fails immediately with a message saying which shape was expected

#### Scenario: The same stage is registered twice

- **WHEN** the same open generic stage is registered more than once
- **THEN** it is installed once, and a dispatched request passes through it once

### Requirement: Commands scoped to one aggregate are serialised against each other

Where a command declares the aggregate it acts on, the framework SHALL ensure that commands naming
the same aggregate do not execute concurrently, while commands naming different aggregates, and
commands that name none, execute in parallel.

Serialising per aggregate is what keeps optimistic-concurrency conflicts to genuine cross-process
contention rather than a host competing with itself.

#### Scenario: Two commands name the same aggregate

- **WHEN** two aggregate-scoped commands naming the same aggregate are dispatched concurrently
- **THEN** the second waits for the first to complete

#### Scenario: Two commands name different aggregates

- **WHEN** two aggregate-scoped commands naming different aggregates are dispatched concurrently
- **THEN** both proceed in parallel

#### Scenario: A command names no aggregate

- **WHEN** commands that are not aggregate-scoped are dispatched concurrently
- **THEN** they proceed in parallel and take no lock at all

#### Scenario: The number of aggregates exceeds the number of locks

- **WHEN** more distinct aggregates are in flight than the framework holds locks for
- **THEN** correctness is preserved — two unrelated aggregates may serialise against each other, but
  two commands on the same aggregate never run concurrently

### Requirement: The routing convention distinguishes synchronous from deferred mutation

A command dispatched in process SHALL complete when its handler has completed. A command dispatched
through the outbox SHALL complete when the intent has been durably recorded, not when the handler
has run.

A caller that needs the outcome must dispatch in process; a caller that dispatches through the
outbox and awaits it has awaited the wrong thing.

#### Scenario: A caller needs the result

- **WHEN** a command with a result is dispatched in process
- **THEN** the caller receives the handler's result

#### Scenario: A caller does not need the result

- **WHEN** a command without a result is dispatched through the outbox
- **THEN** the call returns once the intent is durably recorded, and the handler runs later,
  possibly in another process

#### Scenario: A mutation would destroy the deferred path itself

- **WHEN** a command's effect would destroy the durable store the deferred path depends on
- **THEN** it must be dispatched in process — the deferred path cannot carry a command that removes
  it

### Requirement: A command may be recorded before its handler runs

The framework SHALL offer an opt-in stage that durably records each dispatched command before the
handler executes, so that an audit trail exists even for a command whose handler fails.

#### Scenario: A command is dispatched with auditing enabled

- **WHEN** a command is dispatched and auditing is registered
- **THEN** the command is recorded, and only then does the handler run

#### Scenario: A query is dispatched with auditing enabled

- **WHEN** a request that is not a command is dispatched
- **THEN** nothing is recorded, and the request proceeds

### Requirement: A command can name the owner of its protected fields

A command SHALL be able to name the tenant, and optionally the user, that its fields marked for
encryption belong to, by implementing `INamesDataOwner`. Wherever the framework serializes such a
command — to record it before its handler runs, and to carry it to a handler that runs later — it
SHALL encrypt the marked fields for the named owner and not for the session the command was dispatched
under, and SHALL write every copy for the same tenant and the same user.

The session says who is acting and what they may do. It does not say whose data a command carries: a
command dispatched from a session with no tenant, or from the session of another tenant than the one
its data belongs to, still carries somebody's data, and only the command knows whose.

The record of a command that names an owner SHALL name that owner as its data owner and the session's
actor as its actor, so that a tenant's view of the recorded commands holds the commands that carried
its data, whoever issued them.

A command that implements the declaration and names an empty tenant, or an empty user beside its
tenant, SHALL be refused where it is recorded and where it is dispatched to run later, with an error
that names the command's type, and nothing SHALL be recorded or sent for it. It SHALL NOT fall back to
the session's tenant: a command that says it has an owner and names none is a mistake in the caller,
and encrypting it for whoever is acting would hide the mistake in valid ciphertext.

The owner SHALL be taken as the command states it. The framework does not check that the named tenant
exists, and encrypting for an owner creates key material for it where there was none. The
documentation SHALL say so, and SHALL say that the owner is to be named from something the dispatching
code trusts and not from what a caller supplied.

Naming an owner SHALL NOT change the session a handler runs under, and SHALL NOT grant, check or
replace any authorization. Whether the caller may act on that owner's data is decided as it is for a
command that names none — by the stages the host registered and by the handler — and the
documentation SHALL say so.

A recorded command SHALL remain after its owner is erased, as the evidence that it was issued, by whom
and when; its protected fields SHALL read as absent from then on, because the keys they were written
for are gone. Where the whole command type is marked for encryption rather than individual fields,
the whole recorded payload reads as absent. The documentation of erasure SHALL state both halves.

A command that names no owner SHALL be serialized as it was before a command could name one: its
protected fields are written for the session's data-owner tenant — together with the session's
data-owner user in the record, and with the acting user in the copy carried to a later handler.

#### Scenario: A command naming an owner is dispatched from a session without a tenant

- **WHEN** a host outside development dispatches a command that carries a tenant-scoped field and
  names a tenant as its owner, from a session that names no tenant
- **THEN** the command is accepted, its field is ciphertext written for the named tenant wherever the
  framework stores it, and its handler receives the field's value

#### Scenario: A command naming an owner is dispatched from another tenant's session

- **WHEN** a command that names one tenant as its owner is dispatched from a session whose data owner
  is a different tenant
- **THEN** its protected fields are written for the tenant it names, not for the session's, and the
  handler runs under the dispatching session unchanged

#### Scenario: A command names a user as well

- **WHEN** a command names a tenant and a user as its owner and auditing is registered
- **THEN** the recorded copy and the copy carried to its handler are both written for that tenant and
  that user

#### Scenario: A command naming an owner is recorded

- **WHEN** a command that names an owner is dispatched with auditing registered
- **THEN** its record names that owner as the data owner and the dispatching session's actor as the
  actor

#### Scenario: A command names an empty tenant as its owner

- **WHEN** a command that implements the declaration and names an empty tenant is dispatched
- **THEN** it is refused with an error naming its type, and nothing is recorded or sent

#### Scenario: A command names an empty user beside its tenant

- **WHEN** a command that names a tenant and an empty user as its owner is dispatched
- **THEN** it is refused with an error naming its type, and nothing is recorded or sent

#### Scenario: A command names a tenant nobody has registered

- **WHEN** a command names as its owner a tenant the host has never seen
- **THEN** it is accepted and its protected fields are written under key material created for that
  tenant, because the owner is taken as stated — which is why the documentation tells the dispatching
  code to name an owner it trusts

#### Scenario: The named owner is erased after the command was recorded

- **WHEN** a tenant's erasure is performed after a command naming that tenant as its owner was
  recorded
- **THEN** the record is still there with its type, time, actor and owner, and its protected fields
  read as absent

#### Scenario: Naming an owner does not authorize

- **WHEN** a command that names an owner is dispatched by a caller the host's authorization or tenant
  isolation would refuse
- **THEN** it is refused exactly as it would be without the declaration

#### Scenario: A command names no owner

- **WHEN** a command that does not implement the declaration is dispatched
- **THEN** its protected fields are written for the session's data-owner tenant — in the record
  together with the session's data-owner user, in the copy carried to a later handler together with
  the acting user — as before a command could name an owner

### Requirement: Every dispatch is traced

Dispatching SHALL open a trace span named for the request type, so that a request's path through the
pipeline and its handler is visible without any handler emitting telemetry itself.

Tracing SHALL NOT be a precondition for dispatching. A host that registers the mediator and supplies
no tracing of its own SHALL dispatch requests; in that case the framework SHALL emit the dispatch
spans from its own single activity source, so that a host which subscribes to framework telemetry
receives them without further registration, and a host which subscribes to nothing pays for nothing.
A host that supplies its own tracer SHALL keep it — the framework's fallback is used only in its
absence.

#### Scenario: A request is dispatched while tracing is enabled

- **WHEN** any request is dispatched
- **THEN** a span identifying the request type covers the pipeline and the handler

#### Scenario: A host registers the mediator and nothing else

- **WHEN** a host registers the mediator without registering any tracing infrastructure and
  dispatches a request
- **THEN** the request reaches its handler and the result is returned; nothing fails for want of a
  tracer

#### Scenario: A host without a tracer of its own subscribes to framework telemetry

- **WHEN** a host registers the mediator, supplies no tracer of its own, and subscribes to the
  framework's activity source
- **THEN** each dispatch is visible to that subscription as a span identifying the request type

#### Scenario: A host supplies its own tracer

- **WHEN** a host registers its own tracer before or after registering the mediator
- **THEN** dispatch spans are emitted through that tracer, exactly as before the framework offered a
  fallback
