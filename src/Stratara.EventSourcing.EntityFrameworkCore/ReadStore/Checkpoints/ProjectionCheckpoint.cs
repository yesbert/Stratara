using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Checkpoints;

/// <summary>
/// Where one store-reading consumer resumes in one partition, and which reader's positions it holds.
/// A position from one reader means nothing to another, so a checkpoint written under a different
/// reader is refused rather than misread.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ProjectionCheckpoint
{
    /// <summary>The consumer, by the name the framework gives it.</summary>
    public required string Projection { get; set; }

    /// <summary>The partition.</summary>
    public int Partition { get; set; }

    /// <summary>The position to resume after.</summary>
    public long Position { get; set; }

    /// <summary>The name of the reader whose positions these are.</summary>
    public required string Reader { get; set; }
}
