# Design

## Context

See proposal.md for the motivation. The evidence for the sharing is a measurement on 4.7.1 (six
requests, three claimed by the other product) and a reading of the code for the active flag. The code
holds exactly two writers: the projection replay state (keys `stratara:projection:replay:active`,
`:processed`, `:total`, `:error`, `:request-id`, `:last`, prefix `:claimed:`, channels `:request`,
`:state`) and the Redis outbox lock (`stratara:outbox:lock`). A search of `src/` for database and
subscriber access finds no third. The Orleans grain directory writes to Redis too, but through Orleans
and under the cluster's own identifiers, so it is out of scope. The replay state's Lua scripts name no key
themselves: all three take `KEYS[n]`, so they follow whatever names the callers pass.

## Goals / Non-Goals

**Goals:** one setting for both writers; byte-identical names when it is unset; a host misconfiguration
that can be refused is refused at start.

**Non-Goals:** migrating old keys; detecting at runtime that a host of the same product lacks the name
(hosts that do not meet cannot observe each other); Redis Cluster support, which the unhashed multi-key
scripts do not have today.

## Decisions

**A setting, not `IHostEnvironment.ApplicationName`.** All hosts of a product must agree, and the
application name differs between a product's silo and its admin host. Evidence: the finding's deployment.

**One new options type, `RedisCoordinationOptions`, section `RedisCoordination`.** Alternatives:
`ProjectionReplayOptions.ProductName` would have the lock read the replay's section, and a worker using
only the lock would configure "ProjectionReplay" for it; a property on each of two options is exactly two
settings a host can get wrong. Bound and validated like `ProjectionReplayOptions`, by a binding registered
once from both `AddProjectionReplayState()` and `AddRedisOutboxLock()`.

**Layout `stratara:<name>:<today's suffix>`.** The name follows the shared `stratara:` root, so operators
keep one pattern to scan, and an unset name yields today's names unchanged. Evidence: a unit test pins the
unset names literally.

**Permitted characters: letters, digits, `.`, `_`, `-`.** A `:` could make a name read as part of the key
layout, and `{}` would be a cluster hash tag. Refusing now is cheap; loosening later is additive.
Comparison is exact, so `Acme` and `acme` are different products; the documentation says so.

**Unset stays fixed names (owner's decision).** An installation that sets nothing runs unchanged. The cost
is that the trap of a partly named product remains; it is documented rather than detected, because the two
sides cannot see each other.

## Risks / Trade-offs

- [A product partly rolled out with the name] → The unnamed host receives no request and keeps publishing
  during the named host's replay, and two outbox workers no longer exclude each other. Documentation says
  to set it on all hosts together, with no replay running.
- [A replay active at the moment of the switch] → Its marking stays under the old name until the lease
  lapses, invisible to renamed hosts. Same mitigation.
- [Old keys remain] → `last` and `error` have no lease and stay under the old name. They are harmless, and
  the documentation names them as safe to delete.

## Migration Plan

Nothing for a host that sets nothing. To adopt: choose a name, stop or finish replays, give every host of
the product the name in one deployment. To roll back: remove the name from all hosts together.
