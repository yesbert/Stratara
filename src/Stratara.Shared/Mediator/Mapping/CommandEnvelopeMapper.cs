using System.Text.Json;
using Stratara.Contracts.Messages;
using Stratara.Contracts.Session;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Shared.Reflections;

namespace Stratara.Shared.Mediator.Mapping;

/// <summary>
/// Conversion helper that wraps an in-process <see cref="ICommand"/> instance and the originating
/// <see cref="SessionContext"/> into a wire-level <see cref="CommandEnvelope"/> suitable for the
/// outbox / message bus. Command payload is encrypted through <see cref="ISecureJsonSerializer"/>
/// so any <c>[EncryptData]</c>-decorated fields are end-to-end protected; the session-context envelope
/// remains plaintext because the receiving worker needs to read the routing-relevant tenant id before
/// it has the AAD available to decrypt anything else.
/// </summary>
public static class CommandEnvelopeMapper
{
    /// <summary>
    /// Encrypts <paramref name="command"/> through <paramref name="serializer"/> using the session's
    /// tenant + user as AAD, serializes <paramref name="sessionContext"/> to JSON, and wraps them in a
    /// freshly-id'd <see cref="CommandEnvelope"/>. The envelope id is generated via
    /// <see cref="Guid.CreateVersion7()"/> so it doubles as a sortable outbox row id.
    /// </summary>
    /// <typeparam name="T">CLR type of the command being wrapped.</typeparam>
    /// <param name="command">The command instance to serialize.</param>
    /// <param name="sessionContext">Session context to embed for handler-side replay.</param>
    /// <param name="serializer">The secure JSON serializer used to encrypt <c>[EncryptData]</c>-tagged fields.</param>
    /// <param name="cancellationToken">Token observed during the asynchronous serialization.</param>
    /// <returns>A new <see cref="CommandEnvelope"/> ready to enqueue or publish.</returns>
    public static async Task<CommandEnvelope> MapToAsync<T>(
        this T command,
        SessionContext sessionContext,
        ISecureJsonSerializer serializer,
        CancellationToken cancellationToken = default) where T : ICommand
    {
        var id = Guid.CreateVersion7();
        var commandJson = await serializer.SerializeAsync(command, sessionContext.TenantId, sessionContext.ActorUserId, cancellationToken);
        var sessionContextJson = JsonSerializer.Serialize(sessionContext);

        return new CommandEnvelope(id, commandJson, command.GetType().GetQualifiedTypeName(), sessionContextJson);
    }
}
