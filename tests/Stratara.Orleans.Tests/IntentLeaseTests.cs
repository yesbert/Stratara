using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A command handed over moments after it was recorded is not renewed when its execution starts; one that
/// waited, one the drain resumed, and one whose id carries no record time are.
/// </summary>
public sealed class IntentLeaseTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_command_started_moments_after_it_was_recorded_is_not_renewed_at_the_start()
    {
        var intentId = Guid.CreateVersion7(RecordedAt);

        Assert.False(IntentLease.RenewsAtStart(intentId, RecordedAt + TimeSpan.FromMilliseconds(40), Grace));
    }

    [Fact]
    public void A_command_that_waited_a_quarter_of_the_grace_is_renewed_at_the_start()
    {
        var intentId = Guid.CreateVersion7(RecordedAt);

        Assert.True(IntentLease.RenewsAtStart(intentId, RecordedAt + Grace / 4, Grace));
    }

    [Fact]
    public void A_resumed_command_is_renewed_at_the_start()
    {
        var intentId = Guid.CreateVersion7(RecordedAt);

        Assert.True(IntentLease.RenewsAtStart(intentId, RecordedAt + Grace * 2, Grace));
    }

    [Fact]
    public void A_command_whose_id_carries_no_record_time_is_renewed_at_the_start()
    {
        Assert.True(IntentLease.RenewsAtStart(Guid.NewGuid(), DateTimeOffset.UtcNow, Grace));
    }
}
