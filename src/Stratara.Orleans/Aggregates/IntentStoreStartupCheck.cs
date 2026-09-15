using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Outbox;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Refuses to let a host start that dispatches commands through the execution model without a place
/// to keep their resume bookkeeping. Starting anyway would resume a failing command without a bound.
/// </summary>
internal sealed class IntentStoreStartupCheck(IServiceProviderIsService services) : IHostedService
{
    /// <exception cref="InvalidOperationException">No <see cref="ICommandIntentStore"/> is registered.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!services.IsService(typeof(ICommandIntentStore)))
        {
            throw new InvalidOperationException(
                $"The Orleans command dispatcher is registered, but no {nameof(ICommandIntentStore)} is. Register one, for example with AddStrataraIntentStore<TWriteContext>() from Stratara.Orleans.EntityFrameworkCore.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
