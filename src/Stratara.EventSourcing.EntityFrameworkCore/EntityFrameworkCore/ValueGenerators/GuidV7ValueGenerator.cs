using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Stratara.EventSourcing.EntityFrameworkCore.ValueGenerators;

/// <summary>
/// EF Core <see cref="ValueGenerator{TValue}"/> that produces time-ordered GUID v7 values for
/// primary-key columns, giving inserts B-tree-friendly locality without sacrificing global
/// uniqueness.
/// </summary>
internal sealed class GuidV7ValueGenerator : ValueGenerator<Guid>
{
    /// <inheritdoc/>
    public override bool GeneratesTemporaryValues => false;

    /// <inheritdoc/>
    public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
}
