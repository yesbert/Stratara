using System.Diagnostics.CodeAnalysis;

namespace Stratara.Shared.EventSourcing;

/// <summary>
/// Generic field-level change event that records a single property mutation on
/// <typeparamref name="TAggregate"/>. The generic type parameter acts as a type discriminator so
/// that the framework can route the event back to the correct aggregate handler.
/// </summary>
/// <remarks>
/// <para>
/// Where <paramref name="PropertyName"/> names a property marked for encryption on
/// <typeparamref name="TAggregate"/>, the serialized form of this event carries
/// <paramref name="NewValue"/> as ciphertext at that property's sensitivity level, for the stream's
/// owner, and bound to the name; the name stays readable. Reading the event back yields the value, or
/// <see langword="null"/> once its key is destroyed. A value recorded in the clear is read as stored.
/// </para>
/// <para>
/// A value read back from storage is an untyped JSON element, which
/// <see cref="Extensions.FieldChangedEventExtensions.ApplyPropertyChanged{TAggregate}"/> converts to the property's type.
/// </para>
/// </remarks>
/// <typeparam name="TAggregate">CLR type of the aggregate whose property changed.</typeparam>
/// <param name="PropertyName">Name of the mutated property on the aggregate.</param>
/// <param name="NewValue">
/// The new value to assign (may be <see langword="null"/> for reference / nullable value types). Reads as
/// <see langword="null"/> when the value's key has been destroyed, which replay applies as the property's absent value.
/// </param>
[ExcludeFromCodeCoverage]
public sealed record FieldChangedEvent<TAggregate>(string PropertyName, object? NewValue); // NOSONAR — TAggregate is a type discriminator used by the framework to route events to the correct aggregate handler
