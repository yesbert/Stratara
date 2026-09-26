using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>event-sourcing-store</c> → the owner a stream contributes is the whole owner recorded on its first event, tenant
/// and user, in the save that created the stream and in every later one. The session adds no user to a stream that
/// has none.
/// </summary>
public class EventSourceOwningUserTests
{
    private sealed class OwnedProbe
    {
        public int Value { get; set; }
    }

    private sealed record OwnedProbeTouched(int By);

    private sealed record OwnedProbeOpened(Guid TenantId) : IAggregateCreationEvent;

    private static readonly Guid Tenant = EventStoreTestHost.DefaultTenantId;

    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(services => services
            .AddTrustedType<OwnedProbe>()
            .AddTrustedType<OwnedProbeTouched>()
            .AddTrustedType<OwnedProbeOpened>());

    private static async Task<IReadOnlyList<EventStreamEntry>> ReadStreamAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        return await unitOfWork.CreateEventStreamRepository(transaction).GetManyAsync(streamId);
    }

    private static Task AppendInALaterSaveAsync(EventStoreTestHost host, Guid streamId) =>
        host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<OwnedProbe>(streamId, new OwnedProbeTouched(2));
            await events.SaveChangesAsync();
        });

    [Fact]
    public async Task A_stream_created_for_a_user_keeps_that_user_in_every_later_save()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();
        var owningUser = Guid.NewGuid();

        host.Session.Set(TestSessionContext.ForTenant(Tenant, owningUser));
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<OwnedProbe>(streamId, new OwnedProbeTouched(1));
            await events.SaveChangesAsync();
        });

        host.Session.Set(TestSessionContext.ForTenant(Tenant, Guid.NewGuid()));
        await AppendInALaterSaveAsync(host, streamId);
        host.Session.Set(TestSessionContext.ForTenant(Tenant));
        await AppendInALaterSaveAsync(host, streamId);

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal(3, stream.Count);
        Assert.All(stream, e => Assert.Equal((Tenant, (Guid?)owningUser), (e.TenantId, e.UserId)));
    }

    [Fact]
    public async Task A_stream_created_without_a_user_is_given_none_by_a_session_that_names_one()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<OwnedProbe>(streamId, new OwnedProbeOpened(Tenant));
            await events.SaveChangesAsync();
        });

        host.Session.Set(TestSessionContext.ForTenant(Tenant, Guid.NewGuid()));
        await AppendInALaterSaveAsync(host, streamId);

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal(2, stream.Count);
        Assert.All(stream, e => Assert.Null(e.UserId));
    }

    [Fact]
    public async Task A_stream_first_appended_on_behalf_of_a_user_keeps_that_user_in_a_later_save()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();
        var statedTenant = Guid.NewGuid();
        var statedUser = Guid.NewGuid();

        await host.ExecuteAsync(async events =>
        {
            await events.AppendOnBehalfOfAsync<OwnedProbe>(
                streamId, new OwnedProbeTouched(1), new EventSubject(statedTenant, statedUser));
            await events.SaveChangesAsync();
        });

        await AppendInALaterSaveAsync(host, streamId);

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal(2, stream.Count);
        Assert.All(stream, e => Assert.Equal((statedTenant, (Guid?)statedUser), (e.TenantId, e.UserId)));
    }
}
