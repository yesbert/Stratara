## MODIFIED Requirements

### Requirement: Blob ciphertext is bound to the scope and purpose it was written for

Encrypting a stream SHALL bind the result to both the scope and a caller-supplied purpose, so that
ciphertext written for one purpose in one scope cannot be decrypted as another.

Ciphertext written in the current format SHALL be authenticated against the whole scope — its
sensitivity level, its tenant and its user — and the purpose. A stream in an earlier format SHALL keep
the binding that format was written with: the previous format binds the tenant and the purpose only,
and the format before it may record no purpose at all.

A reader MAY name the purpose it expects, through the `ISecureBlobEncryptor.DecryptAsync` form that
takes one; the framework's encryptor SHALL then fail authentication for a stream written for another
purpose. A reader that names none is given whatever the stream was written for. That form SHALL be a
default interface method, so an existing implementation keeps compiling and does not check the purpose
until it overrides it.

`StrataraBlobEncryptionOptions.WriteV2Blobs`, read from `Stratara:BlobEncryption:WriteV2Blobs` and
`false` by default, SHALL keep new streams in the previous format for a rolling upgrade. Both formats
SHALL be read whatever it is set to. The documentation SHALL state that a release before the current
format cannot read it, and that the option is for as long as such a host still reads what this one
writes.

#### Scenario: A blob round-trips within its own scope

- **WHEN** a stream is encrypted for a scope and purpose and decrypted for the same scope
- **THEN** the original bytes are recovered

#### Scenario: A blob is read under a different scope

- **WHEN** a stream encrypted in the current format is decrypted under a scope that differs in its
  level, its tenant or its user from the one it was written for
- **THEN** decryption fails

#### Scenario: A blob written by an earlier format version is read

- **WHEN** a stream encrypted under a previously shipped stream format is decrypted
- **THEN** it is still readable — a format revision does not strand data already written

#### Scenario: A blob is read for another purpose

- **WHEN** a stream that records its purpose is decrypted by a reader naming another purpose
- **THEN** decryption fails

#### Scenario: A reader names no purpose

- **WHEN** a stream is decrypted under its own scope by a reader that names no purpose
- **THEN** it decrypts, whatever purpose it was written for

#### Scenario: A blob records no purpose

- **WHEN** a stream in an earlier format that records no purpose is decrypted by a reader naming one
- **THEN** it decrypts — there is no purpose to check it against

#### Scenario: An encryptor does not override the purpose-taking form

- **WHEN** an implementation of `ISecureBlobEncryptor` written before that form existed is asked to
  decrypt for a purpose
- **THEN** it compiles and decrypts as it did, without checking the purpose

#### Scenario: A rolling upgrade writes the previous format

- **WHEN** `WriteV2Blobs` is `true` and a stream is encrypted
- **THEN** it is written in the previous format, and a host on the current release reads it

#### Scenario: A blob in the previous format is read under another scope of its tenant

- **WHEN** a stream in the previous format is decrypted under a scope of the same tenant that differs
  in its level or its user
- **THEN** it decrypts, because that format does not bind them

#### Scenario: A blob in the previous format is read under another tenant

- **WHEN** a stream in the previous format is decrypted under a scope of another tenant
- **THEN** decryption fails

### Requirement: One key store serves several processes over shared storage

Where several processes share one key store file, a process SHALL see keys created by another
process, and concurrent creation for the same scope SHALL converge on one key rather than
overwriting each other. A subject one process marks erased SHALL be refused by every other process
sharing the file, including one that was already running.

Processes sharing one file SHALL run the same file format. A key store SHALL refuse to open a file
whose format is newer than the one it knows, rather than read it partially and write it back without
what it did not understand. A process on a release from before a format change cannot know to refuse,
so the framework SHALL state, for each release that changes the format, that every process sharing a
file is upgraded together.

A key another process has revoked, or whose scope another process has erased, SHALL stop resolving at
a process's next lookup, although that process held the key in memory.

#### Scenario: One process creates a key another needs

- **WHEN** one process creates a key for a new scope and a second process, already running, asks
  for that scope's key
- **THEN** the second process resolves it

#### Scenario: Two processes create a key for the same scope at once

- **WHEN** two processes request a key for the same previously unknown scope concurrently
- **THEN** both end up with the same key rather than one silently replacing the other's

#### Scenario: Two processes create keys for different scopes at once

- **WHEN** two processes create keys for different scopes concurrently
- **THEN** both keys survive

#### Scenario: One process marks a subject another is about to write for

- **WHEN** one process marks a subject erased and a second process, already running, then requests a
  key for a scope that names the subject
- **THEN** the second process refuses the request

#### Scenario: A file written by a newer release is opened

- **WHEN** a key store opens a file whose format is newer than the one it knows
- **THEN** it fails to open, naming the format it found and the one it supports, and leaves the file
  unchanged

#### Scenario: One process revokes a key another holds in memory

- **WHEN** a process has resolved a key and another process sharing the file then revokes it
- **THEN** the first process's next lookup does not resolve it — verified on the file-backed key store
  with two instances over one file

#### Scenario: One process erases a scope another holds in memory

- **WHEN** a process has resolved a key and another process sharing the file then erases its scope
- **THEN** the first process's next lookup does not resolve it — verified on the file-backed key store
  with two instances over one file

## ADDED Requirements

### Requirement: A type with marked fields follows its JSON attributes

A type stored field by field, because a property is marked for encryption, SHALL honour its
`System.Text.Json` attributes when written as it does when read. The stored form SHALL keep each
property under its own name, so payloads already stored still read and an upcaster sees the names it
always saw.

#### Scenario: A property is renamed

- **WHEN** a property carrying `[JsonPropertyName]` on such a type is written and read back, whether or
  not any value in the payload is encrypted
- **THEN** its value is recovered, and the stored form holds it under the property's own name

#### Scenario: A property is ignored

- **WHEN** a property carrying `[JsonIgnore]` on such a type is written, unconditionally or under a
  condition its value meets
- **THEN** it is not stored

#### Scenario: A property has its own converter

- **WHEN** a property carrying `[JsonConverter]` on such a type is written
- **THEN** it is stored in the converter's form, and that form is what an upcaster sees

#### Scenario: A payload was stored before the attributes were followed

- **WHEN** a payload stored under the property names, with no property left out, is read
- **THEN** it reads as before, and ordinary JSON under the attributes' names also still reads
