using Stratara.Abstractions.Mediator;

namespace Stratara.Abstractions.Commands;

/// <summary>
/// Command shape for an in-place aggregate update. Carries the target <see cref="AggregateId"/>
/// and the version the caller has seen — handlers compare it against the current stream
/// version to detect concurrent writes.
/// </summary>
public interface IUpdateCommand : ICommand
{
    /// <summary>The aggregate the update targets.</summary>
    Guid AggregateId { get; }

    /// <summary>The aggregate version the caller observed when constructing the command.</summary>
    long SourceVersion { get; }
}
