using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.BackgroundTasks;

/// <summary>
/// Status descriptor for an in-process background task tracked by
/// <see cref="Stratara.Abstractions.BackgroundTasks.IBackgroundTaskQueue"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class BackgroundTaskInfo
{
    /// <summary>Unique task id, assigned on enqueue.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Current lifecycle status.</summary>
    public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Queued;

    /// <summary>Error message when <see cref="Status"/> is <see cref="BackgroundTaskStatus.Failed"/>; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }
}
