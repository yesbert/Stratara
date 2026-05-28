using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;

namespace Stratara.Projections.Tests.Helpers;

public static class EventFactory
{
    public static IEvent<T> Create<T>(T data, Guid? streamId = null,
        Guid? tenantId = null, Guid? userId = null, long version = 1) where T : notnull
    {
        return new Event<T>(
            Id: Guid.NewGuid(),
            Version: version,
            Data: data,
            StreamId: streamId ?? Guid.NewGuid(),
            TenantId: tenantId ?? Guid.NewGuid(),
            UserId: userId ?? Guid.NewGuid());
    }
}
