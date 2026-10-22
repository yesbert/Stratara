namespace Stratara.Orleans.Projections;

/// <summary>How a store-reading grain stood when it answered a wait.</summary>
[GenerateSerializer]
[Alias("Stratara.Orleans.StoreReaderState")]
internal enum StoreReaderState
{
    /// <summary>The reader reads, and its last read and its last entry applied.</summary>
    Advancing,

    /// <summary>The reader is stopped at an entry it cannot apply.</summary>
    StalledOnEntry,

    /// <summary>The reader's last read of the store or of its checkpoint failed.</summary>
    StalledOnRead,

    /// <summary>The reader is paused, or a full replay is active.</summary>
    Suspended,

    /// <summary>The reader reads nothing: its partition is beyond the host's count, or its consumer is not read here.</summary>
    Retired,
}

/// <summary>A store-reading grain's answer to a wait for a position.</summary>
/// <param name="Reached">Whether the reader has applied up to the position and is not suspended.</param>
/// <param name="State">How the reader stood when it answered.</param>
[GenerateSerializer]
[Alias("Stratara.Orleans.StoreReaderProgress")]
[Immutable]
internal sealed record StoreReaderProgress(
    [property: Id(0)] bool Reached,
    [property: Id(1)] StoreReaderState State);

/// <summary>One caller waiting on a grain for its reader to reach a position.</summary>
/// <param name="position">The position the caller waits for.</param>
internal sealed class StoreReaderWaiter(long position)
{
    /// <summary>The position the caller waits for.</summary>
    public long Position => position;

    /// <summary>Completes when the reader may have reached the position, when the budget passed, or when the grain deactivates.</summary>
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
