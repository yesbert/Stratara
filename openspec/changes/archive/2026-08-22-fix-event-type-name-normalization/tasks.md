# Tasks

- [x] Rewrite `TypeNameNormalization.ToVersionIndependent` to parse the name rather than count commas
      (`src/Stratara.Abstractions/Abstractions/Reflections/TypeNameNormalization.cs`).
- [x] Decide and record: do generic event types remain supported, or are they rejected explicitly?
- [x] `TrustedTypeResolver.Register`: fail on a key conflict with two different types rather than
      `TryAdd` discarding the second (finding EV-2).
- [x] Test: a closed generic type registers and resolves.
- [x] Test: two closed generics differing only in outer assembly do not collide.
- [x] Test: an upcaster whose source is a closed generic matches independently of assembly version.
- [x] Test: a conflicting registration fails rather than being discarded.
