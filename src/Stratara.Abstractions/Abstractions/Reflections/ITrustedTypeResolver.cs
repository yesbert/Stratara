namespace Stratara.Abstractions.Reflections;

/// <summary>
/// Resolves runtime <see cref="Type"/> instances from persisted type-name strings against a host-defined
/// allowlist. The framework uses this in code paths that deserialize messages arriving from message buses
/// or event-store rows, where a naive <see cref="Type.GetType(string)"/> against attacker-controlled input
/// would permit instantiation of arbitrary public types loaded into the process.
/// </summary>
/// <remarks>
/// <para>
/// Population happens at startup through the DI scan extensions (
/// <c>AddCommandHandlersFromAssemblyContaining&lt;T&gt;</c>,
/// <c>AddProjectionsFromAssemblyContaining&lt;T&gt;</c>,
/// <c>AddSagasFromAssemblyContaining&lt;T&gt;</c>,
/// <c>AddAggregatesFromAssemblyContaining&lt;T&gt;</c>). Each scan registers the relevant command / event /
/// aggregate types into the resolver. Hosts may also register additional types explicitly via
/// <c>services.AddTrustedType&lt;T&gt;()</c> for types that are produced but never directly handled
/// (e.g. aggregate snapshot types whose aggregates have no projection / saga).
/// </para>
/// <para>
/// Unregistered type names are rejected with <see cref="InvalidOperationException"/>. The resolver normalises
/// the assembly-qualified name to its version-independent form (no <c>Version=</c> / <c>Culture=</c> /
/// <c>PublicKeyToken=</c> tokens) before lookup so events written by a previous package version still resolve
/// after a consumer upgrade.
/// </para>
/// </remarks>
public interface ITrustedTypeResolver
{
    /// <summary>Returns the <see cref="Type"/> matching <paramref name="typeName"/> or throws <see cref="InvalidOperationException"/>.</summary>
    /// <param name="typeName">Assembly-qualified or version-independent type name as persisted with the event / command / aggregate row.</param>
    /// <returns>The registered runtime type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeName"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">No type matching <paramref name="typeName"/> has been registered.</exception>
    Type Resolve(string typeName);

    /// <summary>Attempts to resolve <paramref name="typeName"/> without throwing.</summary>
    /// <param name="typeName">Assembly-qualified or version-independent type name.</param>
    /// <param name="type">Receives the resolved type on success; <c>null</c> on failure.</param>
    /// <returns><c>true</c> if a registered type matched; <c>false</c> otherwise.</returns>
    bool TryResolve(string typeName, out Type? type);

    /// <summary>Registers <paramref name="type"/> in the resolver. Idempotent.</summary>
    /// <param name="type">The type to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <c>null</c>.</exception>
    void Register(Type type);

    /// <summary>Snapshot of every type currently registered with the resolver.</summary>
    /// <remarks>
    /// Returned collection is a point-in-time snapshot — concurrent <see cref="Register"/> calls
    /// after iteration begins are not reflected. Startup validators (for example the encryption
    /// metadata drift guard) use this to walk the full allowlist once at host start.
    /// </remarks>
    IReadOnlyCollection<Type> RegisteredTypes { get; }
}
