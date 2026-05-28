namespace Stratara.Abstractions.Messaging;

/// <summary>
/// Marker for components that consume from the framework's event bus (event-bundle
/// topic). DI uses this to discover + register consumers in the projection / saga
/// workers.
/// </summary>
public interface IEventBusConsumer;
