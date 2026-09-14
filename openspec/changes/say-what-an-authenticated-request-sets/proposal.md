# Say what an authenticated request sets

> **Status:** proposed

## Why

The `session-context` capability promises that for an authenticated HTTP request "the actor and the
data owner are the same principal". What a consumer observes is narrower: the tenant is the same on
both sides and the claimed user is the actor, but the data-owner user is absent. That absence is
deliberate and load-bearing — the scope and the authenticated data of every encrypted field are
derived from the data-owner tenant *and* user, so a framework that filled in the data-owner user
would move every encrypted payload of an HTTP-originated command to a per-user scope and make the
data already written unreadable. The documentation review of 2026-09-14 found the gap; the specification
is the one that is wrong.

## What Changes

- The requirement *An authenticated HTTP request populates the context from its claims* and its
  scenario *An authenticated request arrives* state what the context holds: actor tenant and
  data-owner tenant are the same tenant, the actor user is the claimed user, and the data-owner user
  is absent unless the application sets it.
- The requirement says why the data-owner user is left absent, so a later reader does not "fix" it.
- The `Stratara.Sessions` package README stops describing the context as "Actor=Subject".
- No published behaviour changes. No code, API, configuration or stored data changes.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `session-context`: *An authenticated HTTP request populates the context from its claims* — the
  data-owner user is stated as absent instead of equal to the actor.

## Impact

- `openspec/specs/session-context/spec.md` — one requirement, one scenario.
- `src/Stratara.Sessions/README.md` — the "Actor=Subject" wording (package README, ships in the
  nupkg; text only).
- Round-4 findings tracker entry R4-Arc-004, closed by this change.
- No version bump: a specification and README correction with no code change.
