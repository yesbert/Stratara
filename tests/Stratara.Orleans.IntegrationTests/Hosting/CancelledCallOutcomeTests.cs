using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.IntegrationTests.Hosting;

[Alias("Stratara.Orleans.IntegrationTests.ICommittingGrain")]
public interface ICommittingGrain : IGrainWithStringKey
{
    [Alias("CommitAsync")]
    Task<string> CommitAsync(CancellationToken cancellationToken);
}

/// <summary>A callee that has begun to commit when its caller cancels, and commits to its end.</summary>
public sealed class CommittingGrain : Grain, ICommittingGrain
{
    internal static readonly ConcurrentDictionary<string, TaskCompletionSource> Entered = new();

    public async Task<string> CommitAsync(CancellationToken cancellationToken)
    {
        Entered.GetOrAdd(this.GetPrimaryKeyString(), _ => new TaskCompletionSource()).TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        return "committed";
    }
}

/// <summary>
/// A grain call cancelled while its callee commits reports what the callee did — the framework's registrations have
/// the caller wait for the callee's answer — where Orleans on its own would report a cancellation at once, and a saga
/// step, command or timer handler that committed would look cancelled and run again. On a silo of its own, on
/// localhost clustering.
/// </summary>
public sealed class CancelledCallOutcomeTests
{
    [Fact]
    public async Task A_call_cancelled_while_the_callee_commits_reports_the_commit()
    {
        var outcome = await CallAndCancelAsync(registered: true, siloPort: 11391, gatewayPort: 30391);

        Assert.Equal("committed", outcome);
    }

    /// <summary>What the registration changes: without it the caller sees a cancellation although the callee committed.</summary>
    [Fact]
    public async Task Without_the_registration_the_same_call_reports_a_cancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallAndCancelAsync(registered: false, siloPort: 11392, gatewayPort: 30392));
    }

    private static async Task<string> CallAndCancelAsync(bool registered, int siloPort, int gatewayPort)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort);
            if (registered)
            {
                FrameworkCallCancellation.Register(silo.Services);
            }
        });
        using var host = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await host.StartAsync(timeout.Token);
        try
        {
            var key = Guid.NewGuid().ToString("N");
            var entered = CommittingGrain.Entered.GetOrAdd(key, _ => new TaskCompletionSource());
            using var caller = new CancellationTokenSource();
            var call = host.Services.GetRequiredService<IGrainFactory>().GetGrain<ICommittingGrain>(key).CommitAsync(caller.Token);

            await entered.Task.WaitAsync(timeout.Token);
            await caller.CancelAsync();
            return await call.WaitAsync(timeout.Token);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
