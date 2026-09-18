using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// A recorded command is resumed on the test host's in-memory SQLite intent store as on a production store: one that
/// fails once is resumed and completes, and one that keeps failing runs as often as the delivery bound says and is then
/// kept — the due query, its order and the claim all run on SQLite.
/// </summary>
public sealed class RecordedCommandResumeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_recorded_command_is_resumed_after_a_failure_and_kept_after_its_bound()
    {
        var runs = new ResumedRuns();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(runs)
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddTrustedType<FailingDeposit>()
            .AddScoped<ICommandHandler<FailingDeposit>, FailingDepositHandler>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher());
        var recovers = Guid.NewGuid();
        var keepsFailing = Guid.NewGuid();

        Guid kept;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            await dispatcher.EnqueueCommandAsync(new FailingDeposit(recovers, Failures: 1), TestContext.Current.CancellationToken);
            kept = await dispatcher.EnqueueCommandAsync(new FailingDeposit(keepsFailing, Failures: int.MaxValue), TestContext.Current.CancellationToken);
        }

        Assert.True(await WaitUntilAsync(() => Task.FromResult(runs.Completed.ContainsKey(recovers))), "the command that failed once was not resumed");
        Assert.Equal(2, runs.Completed[recovers]);
        Assert.True(await WaitUntilAsync(async () => (await EntryAsync(host, kept))?.KeptAt is not null), "the failing command was not kept");
        Assert.Equal(3, runs.Runs[keepsFailing]);
    }

    private static async Task<OutboxEntry?> EntryAsync(ExecutionModelTestHost host, Guid id)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync();
        return await context.Set<OutboxEntry>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
