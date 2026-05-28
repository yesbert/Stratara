using Microsoft.Extensions.Hosting;
using Stratara.Sample.OutboxWorker.Outbox;
using Stratara.Abstractions.Messaging;

namespace Stratara.Sample.OutboxWorker.Workers;

public sealed class OutboxDrainWorker(InMemoryOutbox outbox, IMessageBus bus) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public const string CommandsTopic = "commands";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            while (outbox.TryDequeue(out var entry))
            {
                await bus.PublishAsync(CommandsTopic, entry, stoppingToken).ConfigureAwait(false);
            }
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
