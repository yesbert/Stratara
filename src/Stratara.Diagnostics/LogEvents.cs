namespace Stratara.Diagnostics;

/// <summary>
/// Event-ID schema for Stratara's source-generated <c>[LoggerMessage]</c> extensions. Even
/// hundreds = info/debug, <c>_1xx</c> = error. Consumer apps should pick non-overlapping
/// ranges; Stratara reserves the whole 100_000–199_999 block and currently allocates
/// 100_000–116_999. Start consumer event-IDs at 200_000.
/// </summary>
public static class LogEvents
{
    /// <summary>Change-set / aggregate-update event-IDs (100_000s).</summary>
    public static class ChangeSet
    {
        /// <summary>Change-set successfully created from a request.</summary>
        public const int ChangeSetCreated = 100_002;
        /// <summary>Change-set successfully applied to an aggregate.</summary>
        public const int ChangeSetApplied = 100_003;
        /// <summary>No changes detected; the update is a no-op.</summary>
        public const int NoChangesToApplied = 100_004;
        /// <summary>Aggregate not found for the requested id (error).</summary>
        public const int AggregateNotFound = 100_101;
    }

    /// <summary>Background-task queue event-IDs (101_000s).</summary>
    public static class BackgroundTasks
    {
        /// <summary>Queued hosted service has started.</summary>
        public const int QueuedHostedServiceStarted = 101_001;
        /// <summary>Job completed successfully.</summary>
        public const int JobSuccessfulExecuted = 101_002;
        /// <summary>Job failed during execution.</summary>
        public const int JobFailedExecuted = 101_003;
        /// <summary>Queued hosted service has stopped.</summary>
        public const int QueuedHostedServiceStopped = 101_004;
    }

    /// <summary>Event-store append / read event-IDs (102_000s).</summary>
    public static class EventStore
    {
        /// <summary>Append failed (optimistic-concurrency conflict or DB error).</summary>
        public const int AppendFailed = 102_101;
        /// <summary>SaveChanges call to the write store failed.</summary>
        public const int SaveChangesFailed = 102_102;
        /// <summary>Requested event stream not found.</summary>
        public const int StreamNotFound = 102_103;
    }

    /// <summary>Validation event-IDs (103_000s).</summary>
    public static class Validation
    {
        /// <summary>Validation failed on an incoming request.</summary>
        public const int ValidationFailed = 103_101;
        /// <summary>Non-blocking validation failures (<c>Warning</c>/<c>Info</c> severity) were observed; the request still reached the handler (warning).</summary>
        public const int NonBlockingValidationFailures = 103_102;
    }

    /// <summary>Projection worker event-IDs (104_000s).</summary>
    public static class Projection
    {
        /// <summary>Projection worker has started.</summary>
        public const int ProjectionWorkerStarted = 104_001;
        /// <summary>Projection worker has stopped.</summary>
        public const int ProjectionWorkerStopped = 104_002;
        /// <summary>Projection failed processing an event bundle (error).</summary>
        public const int ProjectionFailed = 104_103;
        /// <summary>Received event bundle does not contain events relevant to this projection.</summary>
        public const int EventsNotRelevantForProjection = 104_004;
        /// <summary>Projection replay session has started.</summary>
        public const int ProjectionReplayStarted = 104_005;
        /// <summary>Projection replay session has completed.</summary>
        public const int ProjectionReplayCompleted = 104_006;
        /// <summary>Projection views were truncated as part of replay setup.</summary>
        public const int ProjectionViewsTruncated = 104_007;
        /// <summary>One projection-replay batch published to the projection worker.</summary>
        public const int ProjectionReplayBatchPublished = 104_008;
        /// <summary>Projection replay failed (error).</summary>
        public const int ProjectionReplayFailed = 104_109;
        /// <summary>A bundle refers to an entity no projection has applied yet; it is retried (warning).</summary>
        public const int PrecedingFactMissing = 104_010;
    }

    /// <summary>Command-handling worker event-IDs (105_000s).</summary>
    public static class CommandProcessing
    {
        /// <summary>Command-handling worker has started.</summary>
        public const int CommandWorkerStarted = 105_001;
        /// <summary>Command-handling worker has stopped.</summary>
        public const int CommandWorkerStopped = 105_002;
        /// <summary>A command-worker lane (interactive or heavy) has bound to its topic/subscription.</summary>
        public const int CommandWorkerLaneStarted = 105_005;
        /// <summary>Command envelope integrity-signature verification failed under Permissive mode (warning, payload still dispatched).</summary>
        public const int CommandEnvelopeIntegrityWarning = 105_003;
        /// <summary>Command envelope integrity-signature verification failed under Strict mode (rejected, payload dropped).</summary>
        public const int CommandEnvelopeIntegrityRejected = 105_104;
    }

    /// <summary>Outbox worker event-IDs (106_000s).</summary>
    public static class OutboxProcessing
    {
        /// <summary>Outbox worker has started.</summary>
        public const int OutboxWorkerStarted = 106_001;
        /// <summary>Outbox worker has stopped.</summary>
        public const int OutboxWorkerStopped = 106_002;
        /// <summary>Outbox worker observed a cooperative cancellation.</summary>
        public const int OutboxWorkerOperationCanceled = 106_003;
        /// <summary>Outbox worker failed (error).</summary>
        public const int OutboxWorkerFailed = 106_104;
        /// <summary>Outbox worker skipped a polling cycle because another instance holds the distributed lock.</summary>
        public const int OutboxLockNotAcquired = 106_005;
        /// <summary>The distributed-lock store (e.g. Redis) was unavailable when the worker tried to acquire the outbox lock (warning).</summary>
        public const int OutboxLockUnavailable = 106_106;
        /// <summary>Releasing the outbox distributed lock failed; the key auto-expires on lease end (warning).</summary>
        public const int OutboxLockReleaseFailed = 106_107;
    }

    /// <summary>Event-stream-hash worker event-IDs (107_000s).</summary>
    public static class EventStreamHashing
    {
        /// <summary>Hash worker has started.</summary>
        public const int EventStreamHashWorkerStarted = 107_001;
        /// <summary>Hash worker has stopped.</summary>
        public const int EventStreamHashWorkerStopped = 107_002;
        /// <summary>Hash worker observed a cooperative cancellation.</summary>
        public const int EventStreamHashWorkerOperationCanceled = 107_003;
        /// <summary>Hash worker failed (error).</summary>
        public const int EventStreamHashWorkerFailed = 107_104;
    }

    /// <summary>Messaging event-IDs (108_000s).</summary>
    public static class Messaging
    {
        /// <summary>Message processing failed (error).</summary>
        public const int MessageProcessingFailed = 108_101;
        /// <summary>Message envelope failed to deserialise (error).</summary>
        public const int MessageDeserializationFailed = 108_102;
        /// <summary>Command-envelope dispatch failed (error).</summary>
        public const int CommandEnvelopeDispatchFailed = 108_103;
        /// <summary>Event-bundle dispatch failed (error).</summary>
        public const int EventBundleDispatchFailed = 108_104;
        /// <summary>Concurrency conflict observed; message re-queued for retry.</summary>
        public const int ConcurrencyConflictRequeued = 108_105;
        /// <summary>Subscription cleanup started after a cancellation request.</summary>
        public const int SubscriptionCleanup = 108_006;
        /// <summary>Subscription cleanup failed (warning).</summary>
        public const int SubscriptionCleanupFailed = 108_107;
        /// <summary>RabbitMQ fallback to default guest/guest credentials (warning).</summary>
        public const int RabbitMqGuestFallback = 108_108;
        /// <summary>Publish-channel cleanup (recreation) raised an exception; the channel was re-created anyway (warning).</summary>
        public const int PublishChannelCleanupFailed = 108_109;
    }

    /// <summary>Aggregate-update event-IDs (109_000s).</summary>
    public static class Update
    {
        /// <summary>Update operation failed (error).</summary>
        public const int UpdateFailed = 109_101;
        /// <summary>Update operation succeeded.</summary>
        public const int UpdateSucceeded = 109_102;
        /// <summary>Aggregate was deleted as part of an update operation.</summary>
        public const int AggregateDeleted = 109_103;
        /// <summary>Aggregate resolved to null (error).</summary>
        public const int AggregateNull = 109_104;
    }

    /// <summary>Saga worker event-IDs (110_000s).</summary>
    public static class Saga
    {
        /// <summary>Saga worker has started.</summary>
        public const int SagaWorkerStarted = 110_001;
        /// <summary>Saga worker has stopped.</summary>
        public const int SagaWorkerStopped = 110_002;
        /// <summary>Saga handler failed (error).</summary>
        public const int SagaFailed = 110_103;
        /// <summary>Received event bundle does not contain events relevant to any saga.</summary>
        public const int EventsNotRelevantForSaga = 110_004;
        /// <summary>A bundle refers to an entity no saga has seen yet; it is retried (warning).</summary>
        public const int PrecedingFactMissing = 110_005;
    }

    /// <summary>Event-bundle integrity event-IDs (111_000s) — emitted by every worker that consumes <c>EventBundle</c>s (projection, saga).</summary>
    public static class EventBundleIntegrity
    {
        /// <summary>Event bundle integrity-signature verification failed under Permissive mode (warning, payload still dispatched).</summary>
        public const int IntegrityWarning = 111_003;
        /// <summary>Event bundle integrity-signature verification failed under Strict mode (rejected, payload dropped).</summary>
        public const int IntegrityRejected = 111_104;
    }

    /// <summary>Bus-envelope integrity startup event-IDs (113_000s) — emitted by the integrity-startup probe.</summary>
    public static class BusEnvelopeIntegrity
    {
        /// <summary>BusEnvelopeIntegrity.Mode is Off on a Production host (warning).</summary>
        public const int IntegrityOffInProduction = 113_001;
        /// <summary>BusEnvelopeIntegrity.Mode is Permissive or Strict but no <c>IBusEnvelopeSigner</c> is registered — verification silently no-ops (warning).</summary>
        public const int IntegrityEnabledWithoutSigner = 113_002;
    }

    /// <summary>Tenant-isolation event-IDs (114_000s) — emitted by the mediator tenant-isolation pipeline behavior.</summary>
    public static class TenantIsolation
    {
        /// <summary>A tenant-scoped request named a data-owner tenant that does not match the current session's tenant (rejected).</summary>
        public const int SubjectMismatchRejected = 114_101;
        /// <summary>Strict mode observed a cross-tenant operation (actor tenant ≠ data-owner tenant) that the cross-tenant authorizer did not permit (rejected).</summary>
        public const int CrossTenantRejected = 114_102;
        /// <summary>Strict mode permitted a cross-tenant operation after the cross-tenant authorizer granted it.</summary>
        public const int CrossTenantAllowed = 114_003;
    }

    /// <summary>Key-management event-IDs (112_000s) — emitted by the key-store startup probe and related lifecycle.</summary>
    public static class KeyManagement
    {
        /// <summary>The resolved <c>IKeyStore</c> at host start is the development-only <c>DummyKeyStore</c> (warning).</summary>
        public const int DummyKeyStoreActive = 112_001;
        /// <summary>A new data-encryption-key version was created for a scope.</summary>
        public const int KeyCreated = 112_002;
        /// <summary>A scope's key was rotated — a new current version was created.</summary>
        public const int KeyRotated = 112_003;
        /// <summary>A key version was revoked (single-version crypto-shred).</summary>
        public const int KeyRevoked = 112_004;
        /// <summary>All key versions for a scope were erased (GDPR Art. 17 crypto-shred).</summary>
        public const int ScopeErased = 112_005;
        /// <summary>The in-memory key-store state was reloaded from disk to pick up keys written by another process.</summary>
        public const int KeyStoreReloaded = 112_006;
        /// <summary>A value was encrypted at a level naming a dimension that was absent, so its scope cannot isolate (warning, development only).</summary>
        public const int ScopeCannotIsolate = 112_007;
    }

    /// <summary>External-login provisioning event-IDs (115_000s) — emitted by the JIT external-login provisioning service.</summary>
    public static class ExternalLoginProvisioning
    {
        /// <summary>A first external sign-in provisioned a new local account and linked the external login.</summary>
        public const int UserProvisioned = 115_001;
        /// <summary>An external login was linked to an existing local account after the verified-email checks passed.</summary>
        public const int LoginLinked = 115_002;
        /// <summary>Auto-linking to an existing account was refused because the local or asserted email was not verified — interactive linking is required (fail-closed, nOAuth defense).</summary>
        public const int LinkRefusedUnverified = 115_103;
        /// <summary>Provisioning was denied — auto-provisioning is disabled with no matching account, or the invitation gate rejected the sign-in.</summary>
        public const int ProvisioningDenied = 115_104;
    }

    /// <summary>API-key lifecycle event-IDs (116_000s) — emitted by the API-key store.</summary>
    public static class ApiKeys
    {
        /// <summary>A caller-supplied machine key was imported and is now stored.</summary>
        public const int KeyImported = 116_001;
        /// <summary>An import found the key value already stored and left it untouched (idempotent repeat).</summary>
        public const int KeyImportNoOp = 116_002;
        /// <summary>An import repeated an already-stored key with different parameters; the stored key was left untouched (warning).</summary>
        public const int KeyImportDiffers = 116_103;
    }
}
