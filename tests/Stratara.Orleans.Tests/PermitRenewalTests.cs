using System.Net;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A running unit whose permit is gone reclaims it and reads the answer: a refusal is logged once and the unit runs on,
/// every following renewal reclaims again instead of renewing, and once a reclaim is taken the unit renews as before
/// (scenario <em>A running unit is refused when it registers again</em>).
/// </summary>
public sealed class PermitRenewalTests
{
    private static readonly SiloAddress Holder = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);

    [Fact]
    public async Task A_refused_reclaim_is_logged_once_and_asked_again_until_it_is_taken()
    {
        var unit = Guid.NewGuid();
        var keeper = new ScriptedKeeper(renewals: [false, true], reclaims: [false, false, true]);
        var logger = new RecordingLogger();
        var renewal = new PermitRenewal(keeper, unit, Holder, logger);

        await TickAsync(renewal);
        Assert.False(renewal.Held);
        await TickAsync(renewal);
        Assert.False(renewal.Held);
        await TickAsync(renewal);
        Assert.True(renewal.Held);
        await TickAsync(renewal);

        Assert.Equal(["renew", "reclaim", "reclaim", "reclaim", "renew"], keeper.Calls);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.PermitRenewalLost);
        var refused = Assert.Single(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.PermitReclaimRefused);
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Contains(unit.ToString(), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reclaim_taken_at_once_logs_no_refusal()
    {
        var keeper = new ScriptedKeeper(renewals: [false, true], reclaims: [true]);
        var logger = new RecordingLogger();
        var renewal = new PermitRenewal(keeper, Guid.NewGuid(), Holder, logger);

        await TickAsync(renewal);
        await TickAsync(renewal);

        Assert.True(renewal.Held);
        Assert.Equal(["renew", "reclaim", "renew"], keeper.Calls);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == LogEvents.Orleans.PermitReclaimRefused);
    }

    private static Task TickAsync(PermitRenewal renewal)
    {
        renewal.Tick();
        return renewal.SettleAsync();
    }

    private sealed class ScriptedKeeper(bool[] renewals, bool[] reclaims) : IHeavyWorkPermitGrain
    {
        private readonly Queue<bool> _renewals = new(renewals);
        private readonly Queue<bool> _reclaims = new(reclaims);

        public List<string> Calls { get; } = [];

        public Task<bool> TryAcquireAsync(Guid unitId, SiloAddress holder) => throw new InvalidOperationException("a running unit does not acquire");

        public Task<bool> ReclaimAsync(Guid unitId, SiloAddress holder)
        {
            Calls.Add("reclaim");
            return Task.FromResult(_reclaims.Dequeue());
        }

        public Task<bool> RenewAsync(Guid unitId)
        {
            Calls.Add("renew");
            return Task.FromResult(_renewals.Dequeue());
        }

        public Task ReleaseAsync(Guid unitId) => Task.CompletedTask;

        public Task<int> InUseAsync() => Task.FromResult(0);
    }
}
