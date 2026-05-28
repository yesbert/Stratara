namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Marker for the write-side DbContext that hosts the event-sourcing tables
/// (<c>event_stream_entry</c>, <c>snapshot</c>, <c>command_log_entry</c>, <c>outbox_entry</c>).
/// </summary>
public interface IWriteDbContext : IDbContext;
