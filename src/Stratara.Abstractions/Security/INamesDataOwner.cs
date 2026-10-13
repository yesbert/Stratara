namespace Stratara.Abstractions.Security;

/// <summary>
/// Opt-in declaration on a command that names the tenant, and optionally the user, its fields marked
/// with <see cref="EncryptDataAttribute"/> belong to.
/// </summary>
/// <remarks>
/// <para>
/// A command that implements it has its protected fields encrypted for the named owner wherever the
/// framework serializes it — the copy carried to a handler that runs later and the copy the command
/// audit records — whatever session dispatched it. Both copies are written for the same tenant and the
/// same user, and the audit record names that owner as its data owner and the session's actor as its
/// actor. A command that does not implement it is encrypted for the session it was dispatched under.
/// </para>
/// <para>
/// It is to a command what <c>IAggregateCreationEvent</c> is to a stream's first event: the statement
/// of whose data this is, made by the only party that knows. Use it where the session does not say —
/// a session with no tenant, such as an anonymous endpoint or a saga reacting to a fact one recorded,
/// or a session whose tenant is not the tenant the command's data belongs to.
/// </para>
/// <para>
/// <strong>It authorizes nothing.</strong> The session a handler runs under is the dispatching session,
/// unchanged, and nothing compares the named owner against it. Whether the caller may act on that
/// owner's data is decided as for any other command, by the pipeline stages the host registered and by
/// the handler.
/// </para>
/// <para>
/// An empty <see cref="DataOwnerTenantId"/> is refused when the command is recorded or dispatched, with
/// an <see cref="ArgumentException"/> naming the command type. It does not fall back to the session.
/// </para>
/// <para>
/// A command still waiting when its owner is erased reaches its handler with its protected fields
/// absent, as any record does whose key was destroyed.
/// </para>
/// </remarks>
/// <example>
/// Implement it explicitly from a field the command already carries, so nothing is added to the
/// command's serialized form:
/// <code>
/// public sealed record TrackPageCommand(
///     Guid TenantId,
///     [property: EncryptData(DataSensitivityLevel.TenantScoped)] string Address)
///     : ICommand, INamesDataOwner
/// {
///     Guid INamesDataOwner.DataOwnerTenantId =&gt; TenantId;
/// }
/// </code>
/// </example>
public interface INamesDataOwner
{
    /// <summary>The tenant the command's protected fields belong to. Must not be empty.</summary>
    Guid DataOwnerTenantId { get; }

    /// <summary>
    /// The user the command's protected fields belong to, or <see langword="null"/> when they belong to
    /// the tenant alone. Defaults to <see langword="null"/>.
    /// </summary>
    Guid? DataOwnerUserId => null;
}
