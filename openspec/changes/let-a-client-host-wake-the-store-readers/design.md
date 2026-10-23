## Context

See `proposal.md`, *Why*. Read on `main` at `065d14b` (`v4.4.6`):

- **The wake-up is a bundle dispatcher.** `OrleansEventBundleDispatcher` (`internal`) implements
  `IEventBundleOutboxDispatcher`. After a commit it computes the partitions the bundle touched
  (`PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), PartitionCount)`) and calls every
  registered `INudgeTarget` for each of them. With `hybrid` it also forwards to the kept bus dispatcher
  (`InnerBundleDispatcher`). A nudge that cannot be sent is logged at debug (`LogNudgeFailed`), and the poll
  covers it (`OrleansEventBundleDispatcher.cs:66-87`).
- **Only a store-reading role installs it.** `AddStrataraProjectionGrains` and `AddStrataraSagaGrains`
  call `AddStoreReaderCore` → `ReplaceBundleDispatcher(services, hybrid)`, and each adds its own nudge
  target (`ProjectionNudgeTarget`, `SagaNudgeTarget`). `ReplaceBundleDispatcher` is idempotent and answers
  every `hybrid` request (`OrleansProjectionServiceCollectionExtensions.cs:174-212`).
- **Nudge targets need the silo's composition.** `ProjectionNudgeTarget` names its grains from the
  registered `IProjection`s via `IProjectionHandler.GetProjectionName`. `SagaNudgeTarget` names them from
  `SagaRegistrations`. The grain key is `StoreReaderGrainKey.Of(name, partition)`. A client host registers
  neither, so it cannot address a reader.
- **`INudgeTarget` is also a seeding and reset port.** `StoreReaderSeeding`, `ExecutionModelReset` and
  `ExecutionModelTestHost` enumerate it for consumer names, `EnsureRunningAsync` and `PauseAsync`. A
  client-side wake-up must not appear there.
- **Roles are placed by filter.** `ProjectionsRolePlacementFilterAttribute` and
  `SagasRolePlacementFilterAttribute` keep a grain on silos that published the role in their metadata
  (`RolePlacement.cs`). A call for a role no silo published fails with a message naming the role (spec:
  *No silo registers a role*).
- **A client has a grain factory and no silo metadata.** `UseOrleansClient` gives `IGrainFactory` /
  `IClusterClient`. The roles the silos publish are in silo metadata, which a client does not read.
- **Readers poll.** `ProjectionGrainOptions.PollInterval` and the saga equivalent default to five seconds.
  A lost wake-up costs at most that.

## Goals / Non-Goals

**Goals:**

- One registration that lets any host in the cluster commit without a broker and wake the readers at once.
- No knowledge of projection or saga names outside the silos that host them.
- No per-commit cost for a role the cluster does not host, and no failed commit for any wake-up.

**Non-Goals:**

- Guaranteed delivery of a wake-up. It stays a hint. The store is the truth and the poll is the safety net.
- Changing what a store-reading silo wakes for its own roles.
- Forwarding a client host's in-process mediator commands to a grain. That would be a different execution
  path and a different change.

## Decisions

### D1: A wake-up grain per role and key, placed by the role's filter, fans out on its own silo

Two grains are added, `ProjectionWakeUpGrain` with `[ProjectionsRolePlacementFilter]` and
`SagaWakeUpGrain` with `[SagasRolePlacementFilter]`, both behind one internal interface `IStoreWakeUpGrain`
with an integer key. `WakeAsync(int[] buckets)` is `[AlwaysInterleave]`. On the silo, the grain folds the
buckets into the silo's own partitions and calls the local `INudgeTarget`s of its role for each one. Those
targets know the reader names, because the silo registered the projections and sagas.

*Why a grain on a silo of the role, not names on the client:* the client would need every projection and
saga name. The spec says a role's work runs where it is registered, and names that live in two
compositions drift. The extra hop costs one grain call per role and key per commit, made without awaiting
it on the commit path.

*Alternative rejected: configured consumer names on the client.* It drifts silently: a projection added to
the silo and not to the client's list is never woken, and nothing fails.

*Alternative rejected: one wake-up grain per role.* It becomes a hot spot for every commit in the cluster.

### D2: The key spreads load; the silo decides the partitions

The client sends the touched **buckets** to the grain keyed `bucket % PartitionCount` of its own options
(one call per distinct key). The silo-side grain maps each bucket to its own partition. A client whose
`CommitOrder:PartitionCount` differs from the silos' therefore still wakes the right partitions. Its
setting only decides how calls spread over activations.

*Why:* the partition count is a store-reader setting (`CommitOrderOptions`). Requiring a client host to
mirror it would add a configuration that only fails as latency.

### D3: The dispatcher keeps its nudge targets and gains the cluster wake-ups beside them

*As built. The plan read "a set of `IBundleWakeUp`s instead of `IEnumerable<INudgeTarget>`", and the implementation
kept the existing parameter.* `OrleansEventBundleDispatcher` keeps waking the host's `INudgeTarget`s as before and
takes one more optional dependency, `ClusterWakeUps`, which `AddStrataraStoreWakeUps` registers as a singleton. After
the local nudges it calls `ClusterWakeUps.Wake(grainFactory, streamIds)`, which returns at once. `ClusterWakeUps`
reads `RolePlacement.PublishedRoles` when it is built and leaves out every role the host publishes itself, so a role
a silo hosts is woken locally and only the others through the cluster. The decision is made once the container is
built and does not depend on registration order. `INudgeTarget` stays the seeding and reset port and gains no
client-side member.

*Why not replace the parameter:* the dispatcher's existing tests construct it with targets (`LostNudgeTests`), and
an extra optional dependency leaves every silo without the registration exactly as it was.

### D4: A role no silo hosts is set aside, and a lost wake-up is logged at debug

*As built:* the placement failure is recognised from its message (`RolePlacement.NamesNoSiloFor`), searched through
inner and aggregate exceptions, because it reaches a client through the runtime, which keeps the message but not
always the exception type. The message is built by one helper that both the placement and the check use.


A placement failure that names the role sets that role's cluster wake-up aside for
`StoreWakeUpOptions.AbsentRoleRetry` (default: ten seconds, lowered from one minute in review, because a placement also fails for a moment while a silo of the role joins). The first commit after that tries again. The
transition to absent and back is logged once each at Information (new ids in `LogEvents.Orleans`), so an
operator can see wake-ups going nowhere. Any other failure is a lost nudge, logged at debug with the
existing `LogNudgeFailed`. The commit never awaits a wake-up's answer: the call is observed through a
continuation, as `IntentHandOver.Observe` observes a hand-over.

*Why a request-response call, not `[OneWay]`:* the placement failure is the only signal that a role is
absent. A one-way call would lose it, and the client would call an absent role on every commit.

*Why a new option instead of the reader's poll interval:* the client host does not register the readers'
options, and the time to notice a newly joined role is a separate decision from how often a reader polls.

### D5: The registration mirrors the store-reading ones

`AddStrataraStoreWakeUps(this IServiceCollection services, Action<StoreWakeUpOptions>? configure = null, bool hybrid = false)`:

- guarded by `BuiltHostGuard`;
- registers `CommitOrderOptions` with its validator;
- calls the same `ReplaceBundleDispatcher(services, hybrid)`, so `hybrid` semantics, the error text and
  idempotency are shared;
- adds the two cluster wake-ups once;
- publishes no role.

It sits in `OrleansProjectionServiceCollectionExtensions` beside the two it mirrors.

### D6: Found in review — generations, a bound on outstanding calls, and a refusal where no reader is registered

*As built after `/code-review high` on the pull request.* Each set-aside starts a new generation, and an answer to a
wake-up sent in an earlier generation changes nothing. Without that, a call in flight when the role was set aside
could log the role back while its wake-ups were still skipped. No more than 256 wake-ups of one role await an answer
at once, so a silo that hangs costs the committing host a bounded number of callbacks. A wake-up grain placed on a
silo that registers no reader of its role, which happens where the placement could not filter, refuses with an
exception, so the host logs a lost wake-up instead of taking it as delivered. The targets are resolved once per
activation. The dispatcher and the grain share one nudge loop (`LocalNudges`), and the buckets are computed once per
commit.

*Kept as built: detection from the message.* A dedicated exception type was suggested. The message comes from one
helper that both the placement and the check use, and `ClientWakeUpTests.A_role_no_silo_hosts_is_set_aside…` proves
that it survives the trip to a client on Orleans 10.3.

## Risks / Trade-offs

- **[One more grain call per role per touched key on every commit]** → The calls are fire-and-forget,
  bounded by the number of keys a bundle touches (usually one), and skipped for an absent role. On a
  store-reading silo the roles it hosts stay local (D3).
- **[A wake-up grain sits on a silo that registered the role but not every projection]** → The spec already
  requires every silo of a role to register every projection of the role. A silo that breaks this misses
  wake-ups and its readers poll, as today.
- **[Rolling upgrade: the wake-up grain is unknown to an older silo]** → The call fails as an unknown grain
  type and is logged at debug as a lost nudge. It is not mistaken for an absent role, because only the
  placement failure naming the role sets a role aside. The reader polls.

## Migration Plan

- Upgrade the silos first, then register `AddStrataraStoreWakeUps()` on the hosts that commit in process.
  Only then remove their broker. Until the silos run this release, the wake-ups are lost and the readers
  poll.
- A host that today replaces the bundle dispatcher with its own no-op (as the reporting consumer does)
  removes that replacement and calls the registration instead.
- Rollback: remove the registration and restore the broker, or the no-op. No state is kept.
