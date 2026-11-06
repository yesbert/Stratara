# Spec Delta

## ADDED Requirements

### Requirement: A full replay keeps the saga positions

A full replay on a host whose projections read the store SHALL keep every store-reading saga's position held in the checkpoint store, whichever host runs that saga. A position the host's truncator removed or moved back SHALL be written back, with a warning naming the truncator. A position left in place SHALL NOT be written. A position that cannot be remembered SHALL fail the replay before anything is emptied or reset.

#### Scenario: The truncator empties the checkpoints

- **WHEN** a full replay runs and the host's truncator removes every checkpoint in the read store
- **THEN** each saga position holds the value it had before the truncation, the projections' checkpoints are at the beginning, and a warning names the truncator and the positions written back — verified with the framework's checkpoint store on PostgreSQL

#### Scenario: The truncator leaves the saga positions alone

- **WHEN** a full replay runs and the host's truncator does not touch the saga positions
- **THEN** no saga position is written and no warning is logged

#### Scenario: The replaying host does not run the sagas

- **WHEN** a host that registers no saga replays, and its truncator removes the positions of sagas another host of the deployment runs
- **THEN** those positions are written back too

#### Scenario: A saga reader wrote a lower position in the meantime

- **WHEN** after the truncation a saga's position is held under the same reader at a value lower than the one remembered
- **THEN** the remembered position is written, and a position further on than the remembered one is left as it is

#### Scenario: The truncation fails part-way

- **WHEN** the host's truncator removes saga positions and then fails
- **THEN** the positions are written back and the replay fails with the truncator's failure

#### Scenario: The positions cannot be remembered

- **WHEN** the checkpoint store fails when asked for the saga positions
- **THEN** the replay fails, the truncator has not run, and no checkpoint has been changed

#### Scenario: A position cannot be written back

- **WHEN** writing a saga position back fails
- **THEN** the replay fails, and an error names what could not be written with the value it held, so that an operator can restore it before the sagas read on

#### Scenario: The checkpoint store cannot list positions

- **WHEN** a full replay runs on a checkpoint store that does not implement listing
- **THEN** the replay runs as it did before, and a warning says the saga positions were not guarded and names the store
