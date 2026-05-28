namespace Stratara.Shared.Messaging;

/// <summary>
/// Per-request / per-circuit holder for the client's connection identity. Used by the framework
/// to address SignalR / WebSocket clients (browser tab, mobile session) when a server-side flow
/// needs to push a notification back to the originating channel. Initialized with a synthetic
/// <c>server-{Guid}</c> id so background flows that don't have a real client still have a value.
/// </summary>
public sealed class ClientConnectionIdState
{
    /// <summary>
    /// The active client connection id for the current request / circuit. Defaults to a synthetic
    /// <c>server-{Guid}</c> until <see cref="SetConnectionId"/> overrides it with the channel's id.
    /// </summary>
    public string ConnectionId { get; private set; } = GenerateConnectionId();

    /// <summary>
    /// Overrides the active connection id. Passing <see langword="null"/> regenerates a synthetic
    /// <c>server-{Guid}</c> id (useful when a request has no client channel attached).
    /// </summary>
    /// <param name="connectionId">The new connection id, or <see langword="null"/> to regenerate a synthetic one.</param>
    public void SetConnectionId(string? connectionId)
    {
        ConnectionId = connectionId ?? GenerateConnectionId();
    }

    private static string GenerateConnectionId() => $"server-{Guid.NewGuid()}";
}
