using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A host that dispatches commands through the execution model without an intent store fails at start
/// and names what is missing, instead of resuming a failing command without a bound.
/// </summary>
public sealed class IntentStoreStartupCheckTests
{
    [Fact]
    public async Task A_dispatcher_without_an_intent_store_fails_at_start_naming_it()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddStrataraOrleansCommandDispatcher()
            .BuildServiceProvider();

        var check = provider.GetServices<IHostedService>().OfType<IntentStoreStartupCheck>().Single();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        Assert.Contains(nameof(ICommandIntentStore), refused.Message, StringComparison.Ordinal);
        Assert.Contains("AddStrataraIntentStore", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dispatcher_with_an_intent_store_starts()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddStrataraOrleansCommandDispatcher()
            .AddScoped(_ => new Mock<ICommandIntentStore>().Object)
            .BuildServiceProvider();

        var check = provider.GetServices<IHostedService>().OfType<IntentStoreStartupCheck>().Single();

        await check.StartAsync(CancellationToken.None);
    }
}
