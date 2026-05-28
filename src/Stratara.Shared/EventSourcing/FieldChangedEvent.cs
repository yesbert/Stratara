using System.Diagnostics.CodeAnalysis;

namespace Stratara.Shared.EventSourcing;

/// <summary>
/// Generic field-level change event that records a single property mutation on
/// <typeparamref name="TAggregate"/>. The generic type parameter acts as a type discriminator so
/// that the framework can route the event back to the correct aggregate handler.
/// </summary>
/// <typeparam name="TAggregate">CLR type of the aggregate whose property changed.</typeparam>
/// <param name="PropertyName">Name of the mutated property on the aggregate.</param>
/// <param name="NewValue">The new value to assign (may be <see langword="null"/> for reference / nullable value types).</param>
[ExcludeFromCodeCoverage]
public sealed record FieldChangedEvent<TAggregate>(string PropertyName, object? NewValue); // NOSONAR — TAggregate is a type discriminator used by the framework to route events to the correct aggregate handler
