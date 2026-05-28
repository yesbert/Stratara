namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Marker for components that publish to the framework's event bus (event-bundle
/// topic). DI uses this to wire publishers from event-source save flows.
/// </summary>
public interface IEventBusPublisher;
