## MODIFIED Requirements

### Requirement: The permission vocabulary is declared in code and is strict

An application SHALL declare its permission names and its role-to-permission grants in a catalog at
start-up. Granting a permission that has not been declared SHALL fail immediately, so that a typo
in a grant surfaces at start-up rather than becoming a permission nobody can ever hold.

The catalog MAY be declared in several parts, for example one per module. The parts together SHALL
form one catalog, whatever order they are registered in. A later part SHALL NOT replace an earlier
one: its permissions are added, and its grants to a role accumulate with the grants earlier parts
made to that role. A grant SHALL be checked against the permissions declared by the time it is
made, whichever part declared them. A catalog that was registered in a way the framework cannot add
to SHALL make the next declaration fail at registration, naming the reason, rather than be replaced
or ignored.

#### Scenario: A permission is granted to a role

- **WHEN** a declared permission is granted to a role
- **THEN** that role's grants include it, and further grants to the same role accumulate rather
  than replacing

#### Scenario: An undeclared permission is granted

- **WHEN** a grant names a permission that was never declared
- **THEN** the declaration fails immediately, naming the permission and how to declare it

#### Scenario: A permission is declared twice

- **WHEN** the same permission name is declared more than once
- **THEN** the redeclaration has no effect and does not fail

#### Scenario: A name is empty

- **WHEN** a permission name or a role name is empty or whitespace
- **THEN** it is rejected

#### Scenario: The catalog is declared in two parts

- **WHEN** two parts each declare permissions and grant them to the same role
- **THEN** the role holds the grants of both parts, and a check against a permission of the first
  part is decided as if the second part did not exist

#### Scenario: A later part grants a permission an earlier part declared

- **WHEN** a second part grants a role a permission that only the first part declared
- **THEN** the grant succeeds

#### Scenario: The catalog was registered as a factory

- **WHEN** a host registers the catalog through a factory of its own and then declares a part
- **THEN** the declaration fails at registration, saying that the registered catalog cannot be
  added to
