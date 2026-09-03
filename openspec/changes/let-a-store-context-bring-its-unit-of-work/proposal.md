> **Status:** approved

# Let a store context bring its unit of work

## Why

A consumer who registers the write store the way the documentation shows — one context class
deriving from the framework's write context, one call to the Npgsql factory extension — cannot
dispatch a command. The registration brings the context factory, the context and the default
connection resolver, but not the write-side unit of work that every repository, the event source,
the outbox dispatcher and the command worker take from the container. The first dispatch fails with
a dependency-injection error naming a type the consumer never saw in a guide. The reference page
even says the factory registers "the default `IWriteUnitOfWork` if none is registered"; it does not,
and nothing else in the published packages does — only the test-support host, which is why the
framework's own tests never notice.

The read side has the same gap: the read-context factory registers the factory and the resolver, and
the read-side unit of work the projections capability requires is left to the consumer to construct
by hand from a type they have to discover in the source.

Found on 2026-09-03 while briefing the first example that consumes the published packages rather
than the source. It is the second step of the entry-point work approved that day: the event-sourcing
door has to be "register the context, write an aggregate", not "register the context, read the
framework's test host to learn what else is missing".

## What Changes

- **Registering the write context registers the write-side unit of work.** The Npgsql write-context
  registration also makes the write-side unit of work resolvable for that context, with try-add
  semantics: a consumer that registers its own keeps it.
- **Registering the read context registers the read-side unit of work.** The Npgsql read-context
  registration likewise makes the read-side unit of work — the one projections use, and the plain
  read unit of work it derives from — resolvable, with the same try-add semantics.
- The reference page stops describing behaviour the framework did not have and describes the
  behaviour it now has, for both sides.

Nothing about what the unit of work does changes. A consumer that already registers it by hand sees
no difference; the line becomes deletable.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

- `event-sourcing-store`: gains the requirement that registering the store's database context makes
  the store usable without further registration — the write-side unit of work is available and a
  consumer-supplied one takes precedence.
- `projections`: the requirement *Read models are queried through a scoped unit of work* gains the
  guarantee that registering the read-side database context makes that unit of work available, and
  that a consumer-supplied one takes precedence.

## Impact

- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs`
  — the write and read factory registrations gain the try-add of their unit of work; XML docs say so.
- `docs/reference/di-extensions-cheatsheet.md` — the write row becomes true, the read row says the
  same for its side.
- `llms-full.txt` — regenerated, because it is derived from the XML docs that change.
- `tests/Stratara.EntityFrameworkCore.Tests/` — the registration is pinned on both sides, including
  the precedence of a consumer-supplied unit of work.
- `CHANGELOG.md` — `[Unreleased]`.
- Additive on the published surface: a patch release. Source: consumer briefing for
  `Stratara.Examples`, 2026-09-03 (`.claude/docs/examples-consumer-briefing.md`, item 1).
