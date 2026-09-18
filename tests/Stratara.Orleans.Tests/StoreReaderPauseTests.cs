using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Stratara.Diagnostics;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Change let-a-paused-reader-always-come-back: a store reader's pause belongs to its pauser and lasts only while the
/// pauser renews it. A resume delivered twice releases one pause; an unrenewed pause lapses at its lease and is logged;
/// a renewed one does not; the hold renews at a third of the lease and stops before it resumes.
/// </summary>
public sealed class StoreReaderPauseTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger _logger = new();

    [Fact]
    public void A_resume_delivered_twice_releases_one_pause()
    {
        var pausers = new StoreReaderPausers(_clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        pausers.Hold(first, Lease);
        pausers.Hold(second, Lease);

        Assert.False(pausers.Release(first));
        Assert.False(pausers.Release(first));

        Assert.True(pausers.Any);
        Assert.True(pausers.Holds(second));
        Assert.True(pausers.Release(second));
        Assert.False(pausers.Any);
    }

    [Fact]
    public void An_unrenewed_pause_lapses_at_its_lease_and_is_logged()
    {
        var pausers = new StoreReaderPausers(_clock);
        var pauser = Guid.NewGuid();
        pausers.Hold(pauser, Lease);

        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        Assert.False(pausers.Lapse(_logger, "View", 3));
        Assert.True(pausers.Any);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(pausers.Lapse(_logger, "View", 3));
        Assert.False(pausers.Any);

        var lapse = Assert.Single(_logger.Entries);
        Assert.Equal(LogEvents.Orleans.StoreReaderPauseLapsed, lapse.EventId.Id);
        Assert.Equal(LogLevel.Warning, lapse.Level);
        Assert.Contains("View on partition 3", lapse.Message, StringComparison.Ordinal);
        Assert.Contains(pauser.ToString(), lapse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renewed_pause_does_not_lapse()
    {
        var pausers = new StoreReaderPausers(_clock);
        var pauser = Guid.NewGuid();
        pausers.Hold(pauser, Lease);

        for (var renewal = 0; renewal < 5; renewal++)
        {
            _clock.Advance(Lease / 3);
            pausers.Hold(pauser, Lease);
            Assert.False(pausers.Lapse(_logger, "View", 0));
        }

        Assert.True(pausers.Holds(pauser));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void A_pause_that_lapses_beside_another_leaves_the_reader_paused()
    {
        var pausers = new StoreReaderPausers(_clock);
        var dead = Guid.NewGuid();
        var alive = Guid.NewGuid();
        pausers.Hold(dead, Lease);
        pausers.Hold(alive, Lease * 2);

        _clock.Advance(Lease);

        Assert.False(pausers.Lapse(_logger, "View", 0));
        Assert.True(pausers.Holds(alive));
        Assert.False(pausers.Holds(dead));
        Assert.Single(_logger.Entries);
    }

    [Fact]
    public void A_renewal_after_a_lapse_pauses_again()
    {
        var pausers = new StoreReaderPausers(_clock);
        var pauser = Guid.NewGuid();
        pausers.Hold(pauser, Lease);
        _clock.Advance(Lease);
        Assert.True(pausers.Lapse(_logger, "View", 0));

        pausers.Hold(pauser, Lease);

        Assert.True(pausers.Holds(pauser));
    }

    /// <summary>D4: an older silo's parameterless pause holds for an anonymous pauser, and its resume releases the oldest.</summary>
    [Fact]
    public void An_older_silos_resume_releases_the_oldest_anonymous_pause_and_no_held_one()
    {
        var pausers = new StoreReaderPausers(_clock);
        var held = Guid.NewGuid();
        pausers.HoldAnonymously(StoreReaderGrain.AnonymousLease);
        pausers.Hold(held, Lease);
        pausers.HoldAnonymously(StoreReaderGrain.AnonymousLease);

        Assert.False(pausers.ReleaseOldestAnonymous());
        Assert.False(pausers.ReleaseOldestAnonymous());
        Assert.False(pausers.ReleaseOldestAnonymous());

        Assert.True(pausers.Holds(held));
        Assert.True(pausers.Release(held));
    }

    [Fact]
    public void An_older_silos_pause_lapses_after_ten_minutes()
    {
        var pausers = new StoreReaderPausers(_clock);
        pausers.HoldAnonymously(StoreReaderGrain.AnonymousLease);

        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.False(pausers.Lapse(_logger, "View", 0));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(pausers.Lapse(_logger, "View", 0));
        Assert.False(pausers.ReleaseOldestAnonymous());
    }

    [Fact]
    public async Task A_hold_renews_at_a_third_of_the_lease_and_stops_before_it_resumes()
    {
        var journal = new List<(string Call, Guid Pauser)>();
        var renewed = new SemaphoreSlim(0);
        var reader = Journalled("View/0", journal, renewed);

        var hold = await StoreReaderPause.PauseAllAsync([reader], new StoreReaderLease(Lease, _clock));
        _clock.Advance(Lease / 3);
        Assert.True(await renewed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the hold did not renew at a third of the lease");
        _clock.Advance(Lease / 3);
        Assert.True(await renewed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the hold did not renew a second time");

        await hold.ResumeAsync();
        _clock.Advance(Lease);
        await hold.DisposeAsync();

        Assert.Equal(["pause", "renew", "renew", "resume"], journal.Select(entry => entry.Call));
        Assert.Single(journal.Select(entry => entry.Pauser).Distinct());
    }

    [Fact]
    public async Task A_hold_disposed_without_a_resume_resumes_every_reader_once()
    {
        var journal = new List<(string Call, Guid Pauser)>();
        var readers = new[] { Journalled("View/0", journal, new SemaphoreSlim(0)), Journalled("View/1", journal, new SemaphoreSlim(0)) };

        var hold = await StoreReaderPause.PauseAllAsync(readers, new StoreReaderLease(Lease, _clock));
        await hold.DisposeAsync();
        await hold.DisposeAsync();

        Assert.Equal(2, journal.Count(entry => entry.Call == "resume"));
        Assert.All(journal, entry => Assert.Equal(hold.Pauser, entry.Pauser));
    }

    private static PausedReader Journalled(string name, List<(string Call, Guid Pauser)> journal, SemaphoreSlim renewed) => new(
        name,
        (pauser, _) => Record(journal, "pause", pauser),
        async (pauser, _) =>
        {
            await Record(journal, "renew", pauser);
            renewed.Release();
        },
        pauser => Record(journal, "resume", pauser));

    private static Task Record(List<(string Call, Guid Pauser)> journal, string call, Guid pauser)
    {
        lock (journal)
        {
            journal.Add((call, pauser));
        }

        return Task.CompletedTask;
    }
}
