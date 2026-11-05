using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Sagas;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Timers;

/// <summary>A timer port that serves only the owners whose id starts with its prefix.</summary>
internal interface IPrefixedTimerPort
{
    /// <summary>The start of every owner id this port serves.</summary>
    string OwnerPrefix { get; }
}

/// <summary>
/// Composes the registered timer ports: an owner whose id starts with a prefixed port's prefix is served by
/// that port, and every other owner by the host's own owner check and handler. The ports are collected as
/// they are registered, so the host's may be registered before or after the execution model's.
/// </summary>
internal static class TimerPorts
{
    public static ITimerOwners OwnersFor(IServiceProvider services, string ownerId) =>
        Select(services.GetServices<ITimerOwners>(), ownerId);

    public static ITimerHandler HandlerFor(IServiceProvider services, string ownerId) =>
        Select(services.GetServices<ITimerHandler>(), ownerId);

    private static TPort Select<TPort>(IEnumerable<TPort> ports, string ownerId) where TPort : class
    {
        TPort? host = null;
        foreach (var port in ports)
        {
            if (port is not IPrefixedTimerPort prefixed)
            {
                host ??= port;
            }
            else if (ownerId.StartsWith(prefixed.OwnerPrefix, StringComparison.Ordinal))
            {
                return port;
            }
        }

        return host ?? throw new InvalidOperationException($"No {typeof(TPort).Name} is registered for the timer owner '{ownerId}'.");
    }
}

/// <summary>
/// Refuses to start a host whose timers could reach the wrong port or none: stateful processes registered
/// without the process timers, more than one owner check or handler of the host's own, or one without the other.
/// Whether a saga is a stateful process is read from the type it is registered as, so a saga that needs a session to
/// be built — none is set at start — does not fail the check; only a saga registered through a factory is built to
/// learn its type, and one that cannot be built there is passed over, as the saga reader passes it over.
/// </summary>
internal sealed class TimerPortsStartupCheck(IServiceScopeFactory scopeFactory, IServiceCollection registrations) : IHostedService
{
    /// <summary>Registers the check once, over the composition it reads the registered sagas from.</summary>
    public static void Register(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TimerPortsStartupCheck)))
        {
            return;
        }

        services.AddSingleton(provider => new TimerPortsStartupCheck(provider.GetRequiredService<IServiceScopeFactory>(), services));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<TimerPortsStartupCheck>());
    }

    /// <exception cref="InvalidOperationException">The registered timer ports do not compose.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var owners = services.GetServices<ITimerOwners>().ToList();
        var handlers = services.GetServices<ITimerHandler>().ToList();

        var hostOwners = Unprefixed(owners);
        var hostHandlers = Unprefixed(handlers);
        if (hostOwners.Count > 1 || hostHandlers.Count > 1)
        {
            throw new InvalidOperationException(
                $"More than one {nameof(ITimerOwners)} or {nameof(ITimerHandler)} is registered for the host's timers ({string.Join(", ", hostOwners.Concat(hostHandlers).Distinct())}); a due timer would reach only one of them. Register one owner check and one handler.");
        }

        if (hostOwners.Count != hostHandlers.Count)
        {
            throw new InvalidOperationException(
                $"The host registers {(hostOwners.Count == 0 ? $"an {nameof(ITimerHandler)} without an {nameof(ITimerOwners)}" : $"an {nameof(ITimerOwners)} without an {nameof(ITimerHandler)}")}, so its timers would fail when they fire. Register both.");
        }

        if (owners.OfType<IPrefixedTimerPort>().Any(port => port.OwnerPrefix == SagaProcessTimerHost.Prefix))
        {
            return Task.CompletedTask;
        }

        var processes = registrations
            .Where(d => d.ServiceType == typeof(ISaga) && !d.IsKeyedService)
            .Select(d => SagaTypeOf(d, services))
            .Where(type => type is not null && typeof(ISagaProcess).IsAssignableFrom(type))
            .Select(type => type!.Name)
            .ToList();
        if (processes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Stateful processes are registered ({string.Join(", ", processes)}) but nothing serves their timers. Register AddStrataraSagaGrains.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static Type? SagaTypeOf(ServiceDescriptor descriptor, IServiceProvider services)
    {
        if (descriptor.ImplementationType is { } type)
        {
            return type;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance.GetType();
        }

        try
        {
            return descriptor.ImplementationFactory?.Invoke(services).GetType();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }

    private static List<string> Unprefixed<TPort>(IEnumerable<TPort> ports) =>
        [.. ports.Where(port => port is not IPrefixedTimerPort).Select(port => port!.GetType().Name)];
}
