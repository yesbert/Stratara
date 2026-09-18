# Tasks

## 1. The reset

- [x] 1.1 Move the pause into the store reader every consumer shares, and declare it on the saga's
      grain interface.
- [x] 1.2 The nudge targets stop and start the readers of a partition, so a caller that knows only the
      target can pause them.
- [x] 1.3 The test host's reset stops every registered reader, puts it at the store's head and starts
      it again; the report says how many it moved.
- [x] 1.4 A test that resets a running host and dispatches again: the wait returns, the new fact is
      applied, nothing is applied twice.

## 2. The host

- [x] 2.1 A start that fails stops and disposes what it built.
- [x] 2.2 A start refused because the port was taken is tried again, with a database and ports of its
      own.
- [x] 2.3 The placement filters make the silo's own metadata entries be written before they read them.

## 3. Documentation and the run

- [x] 3.1 The package README and the testing guide say what the reset does to the readers.
- [x] 3.2 `CHANGELOG.md`; the `test-support` spec no longer describes the host's wiring, the reset port
      says what a reset means on a host that goes on running, and the `package-distribution` purpose no
      longer counts packages.
- [x] 3.3 Gauntlet, the testing package's tests and the projection and saga integration suites.
