namespace Stratara.Sample.EventSourced.EventStore;

public interface IProjection
{
    Task HandleAsync(object @event, CancellationToken cancellationToken);
}
