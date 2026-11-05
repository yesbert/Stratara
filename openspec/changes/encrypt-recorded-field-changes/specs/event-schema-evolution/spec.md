## MODIFIED Requirements

### Requirement: Upcasting operates on the payload as it was stored

An upcaster SHALL receive the payload in the form it was persisted. Where fields were marked for
encryption, those field values are ciphertext at that point and are not readable by the upcaster.

An upcaster can therefore rename, move or restructure a protected field, but cannot inspect or
transform its value.

#### Scenario: An upcaster reshapes a protected field

- **WHEN** an upcaster moves or renames a field that was marked for encryption
- **THEN** the transformation applies to the field's position and name, and its value remains
  ciphertext throughout

#### Scenario: An upcaster receives a protected recorded change

- **WHEN** an upcaster receives a field-changed event whose recorded value belongs to a property
  marked for encryption
- **THEN** the property name is readable and the value is ciphertext, and an upcaster that changes
  the property name makes reading the event fail, because the value is bound to the name it was
  recorded under
