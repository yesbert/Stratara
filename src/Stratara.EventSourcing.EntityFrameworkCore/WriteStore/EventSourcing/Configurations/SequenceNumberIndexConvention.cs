using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

/// <summary>
/// Declares the indexes on <see cref="EventStreamEntry.SequenceNumber"/> once the model is final, after the consumer's
/// own configuration has run: a unique index where the event stream's key is not the sequence number — which
/// <c>ApplyGlobalEntityConfiguration</c> makes it <c>Id</c> — and an index over the entries not yet chained, whose
/// filter names the column the model maps the hash to, in the provider's quoting. Both are declared with the
/// precedence of a data annotation: a convention-sourced index over the same column as another is dropped as redundant.
/// </summary>
/// <param name="sqlGenerationHelper">The provider's SQL generation helper, or <see langword="null"/> where the provider is not relational.</param>
internal sealed class SequenceNumberIndexConvention(ISqlGenerationHelper? sqlGenerationHelper) : IModelFinalizingConvention
{
    /// <summary>The unique index on the sequence number, declared where the sequence number is not the key.</summary>
    public const string SequenceNumberIndex = "ix_event_stream_entry_sequence_number";

    /// <summary>The index on the sequence number of the entries whose hash is still null.</summary>
    public const string UnchainedIndex = "ix_event_stream_entry_unhashed_sequence_number";

    /// <inheritdoc/>
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        if (modelBuilder.Metadata.FindEntityType(typeof(EventStreamEntry)) is not { } entry
            || entry.FindProperty(nameof(EventStreamEntry.SequenceNumber)) is not { } sequenceNumber)
        {
            return;
        }

        IReadOnlyList<IConventionProperty> properties = [sequenceNumber];
        if (sqlGenerationHelper is not null
            && entry.FindProperty(nameof(EventStreamEntry.Hash)) is { } hash
            && StoreObjectIdentifier.Create(entry, StoreObjectType.Table) is { } table
            && hash.GetColumnName(table) is { } column)
        {
            entry.Builder.HasIndex(properties, UnchainedIndex, fromDataAnnotation: true)?
                .HasDatabaseName(UnchainedIndex, fromDataAnnotation: true)?
                .HasFilter($"{sqlGenerationHelper.DelimitIdentifier(column)} IS NULL", fromDataAnnotation: true);
        }

        if (entry.FindPrimaryKey() is not { Properties: [var keyProperty] } || keyProperty != sequenceNumber)
        {
            entry.Builder.HasIndex(properties, SequenceNumberIndex, fromDataAnnotation: true)?
                .IsUnique(true, fromDataAnnotation: true)?
                .HasDatabaseName(SequenceNumberIndex, fromDataAnnotation: true);
        }
    }
}
