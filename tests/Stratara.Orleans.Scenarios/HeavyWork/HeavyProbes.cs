using Stratara.Abstractions.Mediator;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>An interactive command whose handler returns at once, measured beside a heavy burst.</summary>
public sealed record InteractiveProbe(Guid AggregateId) : ICommand, IAggregateScopedCommand;

/// <summary>A heavy command whose handler waits for as long as it says.</summary>
public sealed record HeavyProbe(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand, IHeavyCommand;

public sealed class InteractiveProbeHandler : ICommandHandler<InteractiveProbe>
{
    public Task HandleAsync(InteractiveProbe command, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class HeavyProbeHandler : ICommandHandler<HeavyProbe>
{
    public Task HandleAsync(HeavyProbe command, CancellationToken cancellationToken) => Task.Delay(command.DelayMs, cancellationToken);
}
