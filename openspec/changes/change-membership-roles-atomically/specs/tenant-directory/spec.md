## ADDED Requirements

### Requirement: Single roles of a membership can be added and removed

The membership store SHALL offer an operation that adds roles to one membership and one that removes
roles from it. Each SHALL change only the roles it names, leave the membership's other roles and its
status as they were, and return the membership as it stands afterwards. Neither SHALL create a
membership: where the user has none in that tenant, the operation SHALL change nothing and return
none.

#### Scenario: A role is added

- **WHEN** a role is added to a membership that holds other roles
- **THEN** the membership holds the added role and every role it held before, and its status is
  unchanged

#### Scenario: A role the membership already holds is added

- **WHEN** a role is added that the membership already holds
- **THEN** the membership holds that role once, and nothing else about it changes

#### Scenario: A role is removed

- **WHEN** a role is removed from a membership
- **THEN** the membership no longer holds it and still holds every other role it held

#### Scenario: A role the membership does not hold is removed

- **WHEN** a role is removed that the membership does not hold
- **THEN** the membership is unchanged

#### Scenario: Roles are changed where there is no membership

- **WHEN** a role is added to or removed from a user and tenant with no membership
- **THEN** no membership is created and the operation reports that there is none

#### Scenario: Roles of a pending membership are changed

- **WHEN** a role is added to a pending membership
- **THEN** the membership records the role and stays pending, so the role confers nothing until it is
  activated

### Requirement: Overlapping role changes to one membership are not lost

On the framework's membership stores, two role changes to the same membership that run at the same
time SHALL both take effect, whichever finishes first; neither SHALL silently undo the other. Where a
store cannot apply a change after repeatedly losing to concurrent writers, it SHALL fail the
operation rather than drop the change.

#### Scenario: Two different roles are added at the same time

- **WHEN** two callers add two different roles to one membership concurrently
- **THEN** afterwards the membership holds both roles, on the relational directory store and on the
  in-memory double

#### Scenario: A role is added while another is removed

- **WHEN** one caller adds a role to a membership while another removes a different role from it
- **THEN** afterwards the membership holds the added role and not the removed one

#### Scenario: The directory is hosted in the consumer's context

- **WHEN** a consumer that hosts the directory tables in their own context takes this release
- **THEN** their context's model is unchanged and they add no migration

### Requirement: A consumer's own membership store gets the role operations unchanged

A membership store the consumer implemented themselves SHALL receive both role operations without
changing its code. That fallback reads the membership and writes it back whole, and SHALL be
documented as carrying no protection against a concurrent writer.

#### Scenario: A consumer's own membership store

- **WHEN** a consumer's membership store written before these operations existed is compiled against
  the new release
- **THEN** it compiles unchanged and adding or removing a role through it works by reading the
  membership and writing it back whole
