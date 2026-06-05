using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Testing;

/// <summary>
/// Builds <see cref="IEvent{TData}"/> wrappers around event payloads for tests, so projection and
/// saga handlers that take <c>IEvent&lt;TData&gt;</c> can be fed an event with realistic metadata
/// (id, version, stream id, tenant) without going through the event store.
/// </summary>
public static class TestEvent
{
    /// <summary>Wrap <paramref name="data"/> in an <see cref="IEvent{TData}"/> with the given (or generated) metadata.</summary>
    /// <typeparam name="TData">The event payload type.</typeparam>
    /// <param name="data">The event payload.</param>
    /// <param name="streamId">The stream id, or <see langword="null"/> for a fresh one.</param>
    /// <param name="version">The aggregate-relative version (defaults to 1).</param>
    /// <param name="tenantId">The Subject (data-owner) tenant id, or <see langword="null"/> for empty.</param>
    /// <param name="userId">The actor user id, or <see langword="null"/> for empty.</param>
    /// <returns>A populated <see cref="IEvent{TData}"/>.</returns>
    public static IEvent<TData> Create<TData>(
        TData data,
        Guid? streamId = null,
        long version = 1,
        Guid? tenantId = null,
        Guid? userId = null)
        where TData : notnull
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Event<TData>(
            Guid.CreateVersion7(),
            version,
            data,
            streamId ?? Guid.CreateVersion7(),
            tenantId ?? Guid.Empty,
            userId ?? Guid.Empty);
    }
}
