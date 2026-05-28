using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Sample.OutboxWorker.Outbox;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;

namespace Stratara.Sample.OutboxWorker.Workers;

public sealed class MediatorCommandWorker(IMessageBus bus, IServiceProvider services) : BackgroundService
{
    private static readonly MethodInfo HandleAsyncOpenGeneric =
        typeof(IMediator).GetMethods()
            .First(m => m.Name == nameof(IMediator.HandleAsync)
                        && m.IsGenericMethod
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType.IsGenericParameter);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<OutboxEntry>(OutboxDrainWorker.CommandsTopic, "command-worker",
            entry => DispatchAsync(entry, stoppingToken), stoppingToken);

    private async Task DispatchAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        var commandType = Type.GetType(entry.CommandTypeName, throwOnError: true)!;
        var command = JsonSerializer.Deserialize(entry.PayloadJson, commandType)!;

        await using var scope = services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var handleAsync = HandleAsyncOpenGeneric.MakeGenericMethod(commandType);
        await (Task)handleAsync.Invoke(mediator, [command, cancellationToken])!;
    }
}
