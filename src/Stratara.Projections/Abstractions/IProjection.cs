namespace Stratara.Projections.Abstractions;

/// <summary>
/// Marker interface for a Stratara projection (a read-model writer that reacts to events flowing through
/// the event bundle). Implementations declare one or more <c>HandleAsync(TEvent, CancellationToken)</c>
/// methods — public or private — that the <c>ProjectionMethodInvoker</c> discovers via reflection.
/// </summary>
/// <remarks>
/// Projections are registered as scoped services. A new instance is resolved per event bundle, so cross-bundle
/// state must be persisted in the read store. Handler methods may accept either the event payload type
/// (<c>HandleAsync(MyEvent, CancellationToken)</c>) or the wrapping <c>IEvent&lt;MyEvent&gt;</c> when access
/// to metadata (<c>StreamId</c>, <c>Version</c>, …) is required. Mark handler methods with
/// <c>[JetBrains.Annotations.UsedImplicitly]</c> to suppress unused-member warnings.
/// </remarks>
public interface IProjection;
