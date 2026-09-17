using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Where a partly applied batch resumes: at the last applied position below the first unapplied entry, so an entry
/// that shares its position with the failing one — another entry of the same transaction — is read again rather than
/// skipped.
/// </summary>
public sealed class CommittedBatchTests
{
    private const long Before = 10;

    [Fact]
    public void Nothing_applied_resumes_at_the_position_before_the_batch()
    {
        var batch = Batch(11, 12, 13);

        Assert.Equal(Before, batch.ResumePositionBefore(0, Before));
    }

    [Fact]
    public void After_distinct_positions_it_resumes_at_the_last_applied_one()
    {
        var batch = Batch(11, 12, 13);

        Assert.Equal(12, batch.ResumePositionBefore(2, Before));
    }

    [Fact]
    public void A_failing_entry_that_shares_its_position_resumes_below_the_group()
    {
        var batch = Batch(11, 12, 12, 13);

        Assert.Equal(11, batch.ResumePositionBefore(2, Before));
    }

    [Fact]
    public void A_group_that_opens_the_batch_resumes_at_the_position_before_the_batch()
    {
        var batch = Batch(11, 11, 11, 12);

        Assert.Equal(Before, batch.ResumePositionBefore(2, Before));
    }

    internal static CommittedBatch Batch(params long[] positions) =>
        new([.. positions.Select(position => new CommittedEntry(new EventStreamEntry { StreamId = Guid.NewGuid(), Version = 1, EventTypeName = "Probe", AggregateTypeName = "Probe", DataJson = "{}", BucketId = 0, TenantId = Guid.NewGuid(), ActorTenantId = Guid.NewGuid(), ActorUserId = Guid.NewGuid() }, position))], positions[^1]);
}
