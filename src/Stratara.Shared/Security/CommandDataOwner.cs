using Stratara.Abstractions.Security;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;

namespace Stratara.Shared.Security;

/// <summary>
/// The tenant and user a command's protected fields are encrypted for — the one answer every place
/// that serializes or deserializes a command asks for, so that no two of them can disagree.
/// </summary>
/// <remarks>
/// A command that implements <see cref="INamesDataOwner"/> is written for the owner it names, in every
/// copy. A command that does not is written for the session it was dispatched under: the copy carried
/// to a handler for the session's data-owner tenant and acting user, the recorded copy for the
/// session's data-owner tenant and data-owner user.
/// </remarks>
/// <param name="TenantId">The tenant the fields are encrypted for.</param>
/// <param name="UserId">The user the fields are encrypted for, or <see langword="null"/>.</param>
/// <param name="Named">
/// <see langword="true"/> when the command named this owner; <see langword="false"/> when it was taken
/// from the session.
/// </param>
public readonly record struct CommandDataOwner(Guid TenantId, Guid? UserId, bool Named)
{
    /// <summary>
    /// The tenant a carrier of the command records beside its session: <see cref="TenantId"/> when the
    /// command named its owner, <see langword="null"/> when the owner was taken from the session.
    /// </summary>
    public Guid? NamedTenantId => Named ? TenantId : null;

    /// <summary>
    /// The user a carrier of the command records beside its session: <see cref="UserId"/> when the
    /// command named its owner, <see langword="null"/> when the owner was taken from the session.
    /// </summary>
    public Guid? NamedUserId => Named ? UserId : null;

    /// <summary>
    /// The owner the copy of <paramref name="command"/> that travels to a handler is written for.
    /// </summary>
    /// <param name="command">The command about to be serialized.</param>
    /// <param name="session">The session the command is dispatched under.</param>
    /// <returns>The named owner, or the session's data-owner tenant and acting user.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> or <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> implements <see cref="INamesDataOwner"/> and names an empty tenant.</exception>
    public static CommandDataOwner ForTransit(object command, SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(session);

        return NamedBy(command) ?? new CommandDataOwner(session.TenantId, session.ActorUserId, Named: false);
    }

    /// <summary>
    /// The owner the recorded copy of <paramref name="command"/> is written for and attributed to.
    /// </summary>
    /// <param name="command">The command about to be recorded.</param>
    /// <param name="session">The session the command is dispatched under.</param>
    /// <returns>The named owner, or the session's data-owner tenant and data-owner user.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> or <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> implements <see cref="INamesDataOwner"/> and names an empty tenant.</exception>
    public static CommandDataOwner ForRecord(object command, SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(session);

        return NamedBy(command) ?? new CommandDataOwner(session.TenantId, session.UserId, Named: false);
    }

    /// <summary>
    /// The owner a received <paramref name="envelope"/>'s command is read for.
    /// </summary>
    /// <param name="envelope">The envelope whose command is about to be deserialized.</param>
    /// <param name="session">The session the envelope carries.</param>
    /// <returns>The owner the envelope carries, or the session's data-owner tenant and acting user.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> or <paramref name="session"/> is <see langword="null"/>.</exception>
    public static CommandDataOwner ForRead(CommandEnvelope envelope, SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return ForRead(envelope.DataOwnerTenantId, envelope.DataOwnerUserId, session);
    }

    /// <summary>
    /// The owner a received command is read for, from the owner its carrier holds.
    /// </summary>
    /// <param name="dataOwnerTenantId">The tenant the carrier names, or <see langword="null"/> when it names no owner.</param>
    /// <param name="dataOwnerUserId">The user the carrier names beside the tenant, or <see langword="null"/>.</param>
    /// <param name="session">The session the carrier holds.</param>
    /// <returns>The carried owner, or the session's data-owner tenant and acting user.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public static CommandDataOwner ForRead(Guid? dataOwnerTenantId, Guid? dataOwnerUserId, SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return dataOwnerTenantId is { } tenantId
            ? new CommandDataOwner(tenantId, dataOwnerUserId, Named: true)
            : new CommandDataOwner(session.TenantId, session.ActorUserId, Named: false);
    }

    private static CommandDataOwner? NamedBy(object command)
    {
        if (command is not INamesDataOwner named)
        {
            return null;
        }

        if (named.DataOwnerTenantId == Guid.Empty)
        {
            throw new ArgumentException(
                $"{command.GetType().FullName} implements {nameof(INamesDataOwner)} and names an empty tenant as the owner of its protected fields. " +
                "A command that names its data owner must name one: fill the field the declaration reads, " +
                $"or remove {nameof(INamesDataOwner)} from the command to have its fields encrypted for the session.",
                nameof(command));
        }

        return new CommandDataOwner(named.DataOwnerTenantId, named.DataOwnerUserId, Named: true);
    }
}
