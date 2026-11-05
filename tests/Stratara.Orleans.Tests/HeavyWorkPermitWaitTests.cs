using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Orleans.Runtime;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A unit waiting for its permit keeps waiting when the silo keeping the permits cannot be reached, as it waits when the
/// bound is full: the failed call never reaches the command's run, so it is not recorded as a failed attempt of a handler
/// that never ran (requirement <em>Waiting costs a recorded command no attempt</em>). The silo's stop still ends the wait.
/// </summary>
public sealed class HeavyWorkPermitWaitTests
{
    private static readonly SiloAddress Holder = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);

    [Fact]
    public async Task A_keeper_that_cannot_be_reached_while_a_unit_waits_is_asked_again_until_it_grants_the_permit()
    {
        var keeper = new ScriptedKeeper(
            () => throw new SiloUnavailableException(),
            () => throw new TimeoutException(),
            () => false,
            () => true);
        var runner = RunnerOver(keeper);
        var ran = 0;

        await runner.UnderPermitAsync(() =>
        {
            ran++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, ran);
        Assert.Equal(4, keeper.Acquires);
        Assert.Equal(1, keeper.Releases);
    }

    [Fact]
    public async Task A_fault_of_the_keeper_itself_is_not_waited_out()
    {
        var keeper = new ScriptedKeeper(() => throw new InvalidOperationException("keeper fault"));
        var runner = RunnerOver(keeper);
        var ran = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.UnderPermitAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));

        Assert.False(ran);
        Assert.Equal(1, keeper.Acquires);
    }

    [Fact]
    public async Task The_silos_stop_ends_a_wait_whose_keeper_cannot_be_reached()
    {
        using var stopping = new CancellationTokenSource();
        var keeper = new ScriptedKeeper(() =>
        {
            stopping.Cancel();
            throw new SiloUnavailableException();
        });
        var runner = RunnerOver(keeper);
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.UnderPermitAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }, stopping.Token));

        Assert.False(ran);
        Assert.Equal(1, keeper.Acquires);
    }

    private static HeavyWorkRunner RunnerOver(IHeavyWorkPermitGrain keeper)
    {
        var silo = new Mock<ILocalSiloDetails>();
        silo.SetupGet(s => s.SiloAddress).Returns(Holder);
        var grains = new Mock<IGrainFactory>();
        grains.Setup(f => f.GetGrain<IHeavyWorkPermitGrain>(It.IsAny<long>(), null)).Returns(keeper);
        var services = new ServiceCollection()
            .AddSingleton(silo.Object)
            .AddSingleton(grains.Object)
            .BuildServiceProvider();
        var options = Options.Create(new HeavyWorkOptions { PermitRetry = TimeSpan.FromMilliseconds(1) });
        return new HeavyWorkRunner(services, options, TimeProvider.System, NullLogger<HeavyWorkRunner>.Instance);
    }

    private sealed class ScriptedKeeper(params Func<bool>[] answers) : IHeavyWorkPermitGrain
    {
        public int Acquires { get; private set; }

        public int Releases { get; private set; }

        public Task<bool> TryAcquireAsync(Guid unitId, SiloAddress holder)
        {
            var answer = answers[Math.Min(Acquires, answers.Length - 1)];
            Acquires++;
            return Task.FromResult(answer());
        }

        public Task<bool> ReclaimAsync(Guid unitId, SiloAddress holder) => Task.FromResult(true);

        public Task<bool> RenewAsync(Guid unitId) => Task.FromResult(true);

        public Task ReleaseAsync(Guid unitId)
        {
            Releases++;
            return Task.CompletedTask;
        }

        public Task<int> InUseAsync() => Task.FromResult(0);
    }
}
