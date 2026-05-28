namespace Stratara.Abstractions.BackgroundTasks;

/// <summary>Lifecycle states of a queued background task.</summary>
public enum BackgroundTaskStatus
{
    /// <summary>Enqueued, awaiting a worker pickup.</summary>
    Queued,
    /// <summary>Picked up by a worker and executing.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Completed,
    /// <summary>Threw an exception during execution.</summary>
    Failed
}
