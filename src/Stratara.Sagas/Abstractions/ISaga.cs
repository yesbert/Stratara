namespace Stratara.Sagas.Abstractions;

/// <summary>
/// Marker interface for a Stratara saga (a process manager that reacts to events flowing through the
/// event bundle). Implementations declare one or more <c>HandleAsync(TEvent, CancellationToken)</c>
/// methods — public or private — that the <c>SagaMethodInvoker</c> discovers via reflection.
/// </summary>
/// <remarks>
/// Sagas are registered as scoped services. A new instance is resolved per event bundle, so cross-bundle
/// state must be persisted externally (event store, read store, aggregate). Mark handler methods with
/// <c>[JetBrains.Annotations.UsedImplicitly]</c> to suppress unused-member warnings.
/// </remarks>
public interface ISaga;
