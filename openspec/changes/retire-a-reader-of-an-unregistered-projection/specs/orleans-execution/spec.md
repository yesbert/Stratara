## ADDED Requirements

### Requirement: A reader of a consumer the host does not register retires

Where a host has registered the Orleans execution model, a store reader of a projection or a saga that is brought
back — by its keep-alive or by a call — on a silo that registers no projection or saga of its name SHALL retire
itself when it is activated. It SHALL stop being brought back by its keep-alive, read nothing, count no stall, log
once that it retired, naming the consumer and the partition, and not stay activated on that silo, so that a silo
that still registers the consumer can host it. Removing or renaming a projection or a saga SHALL therefore leave no
reader that returns every keep-alive period to read an entry and fail it. A retired reader SHALL leave its
checkpoints where they were, and a host that registers the consumer again SHALL resume it from them rather than
from the beginning of the store. The documentation SHALL describe the retirement and its log event, and SHALL say
how a host removes the checkpoints of a consumer it no longer registers, which a reset leaves in place.

#### Scenario: A host is started without a projection it ran before

- **WHEN** a host whose store holds the checkpoints and keep-alives of a projection's readers is started without
  that projection registered
- **THEN** each of that projection's readers retires on its first activation, logs the retirement once, and is no
  longer brought back by its keep-alive, the silo logs no undelivered reminder tick for those readers, and no entry
  is read or refused for that projection and no stall is counted for it. Verified on the PostgreSQL store with the
  native reader

#### Scenario: Facts are committed after a projection's readers retired

- **WHEN** facts are committed in every partition after the readers of a projection the host no longer registers
  have retired
- **THEN** the projections the host registers apply them, and nothing is read or refused for the retired
  projection

#### Scenario: A projection is registered again

- **WHEN** a host registers a projection again whose readers retired on an earlier start
- **THEN** its readers resume from the checkpoints they held, apply only the facts committed after them, and are
  brought back by their keep-alive again

#### Scenario: A saga is removed or renamed

- **WHEN** a reader of a saga is brought back on a silo that registers no saga of that name
- **THEN** it retires in the same way, logs the retirement once naming the saga and the partition, the silo logs
  no undelivered reminder tick for it, and its checkpoints stay where they were

#### Scenario: The documentation names an orphaned consumer's checkpoints

- **WHEN** an operator reads the operating guide after removing a projection or a saga
- **THEN** it names the event a retired reader logs and says how to remove the checkpoints the consumer left,
  which a reset of the host does not remove
