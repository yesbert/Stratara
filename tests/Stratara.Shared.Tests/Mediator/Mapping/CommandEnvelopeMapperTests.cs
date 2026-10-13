using System.Text.Json;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Contracts.Session;
using Stratara.Shared.Mediator.Mapping;

namespace Stratara.Shared.Tests.Mediator.Mapping;

/// <summary>
/// <c>outbox-and-messaging</c> → a deferred command carries the owner it names, and a command that
/// names none is published as before.
/// </summary>
public class CommandEnvelopeMapperTests
{
    private static readonly Guid SessionTenant = Guid.CreateVersion7();
    private static readonly Guid ActorUser = Guid.CreateVersion7();
    private static readonly Guid OwnerTenant = Guid.CreateVersion7();
    private static readonly Guid OwnerUser = Guid.CreateVersion7();

    private static readonly SessionContext Session =
        new("correlation", "causation", null, Guid.CreateVersion7(), ActorUser, SessionTenant, Guid.CreateVersion7());

    public sealed record Plain(string Text) : ICommand;

    public sealed record Owned(Guid Tenant, Guid? User, string Text) : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;

        Guid? INamesDataOwner.DataOwnerUserId => User;
    }

    [Fact]
    public async Task MapToAsync_CommandNamingAnOwner_SerializesForThatOwnerAndStampsTheEnvelope()
    {
        var serializer = new RecordingSerializer();

        var envelope = await new Owned(OwnerTenant, OwnerUser, "x").MapToAsync(Session, serializer, TestContext.Current.CancellationToken);

        Assert.Equal((OwnerTenant, (Guid?)OwnerUser), Assert.Single(serializer.WrittenFor));
        Assert.Equal(OwnerTenant, envelope.DataOwnerTenantId);
        Assert.Equal(OwnerUser, envelope.DataOwnerUserId);
    }

    [Fact]
    public async Task MapToAsync_CommandNamingATenantAlone_StampsNoUser()
    {
        var serializer = new RecordingSerializer();

        var envelope = await new Owned(OwnerTenant, null, "x").MapToAsync(Session, serializer, TestContext.Current.CancellationToken);

        Assert.Equal((OwnerTenant, (Guid?)null), Assert.Single(serializer.WrittenFor));
        Assert.Equal(OwnerTenant, envelope.DataOwnerTenantId);
        Assert.Null(envelope.DataOwnerUserId);
    }

    [Fact]
    public async Task MapToAsync_CommandNamingNone_SerializesForSessionTenantAndActorUserAndStampsNothing()
    {
        var serializer = new RecordingSerializer();

        var envelope = await new Plain("x").MapToAsync(Session, serializer, TestContext.Current.CancellationToken);

        Assert.Equal((SessionTenant, (Guid?)ActorUser), Assert.Single(serializer.WrittenFor));
        Assert.Null(envelope.DataOwnerTenantId);
        Assert.Null(envelope.DataOwnerUserId);
        Assert.DoesNotContain("DataOwner", JsonSerializer.Serialize(envelope));
    }

    /// <summary>The session is carried as it was dispatched: naming an owner does not rewrite it.</summary>
    [Fact]
    public async Task MapToAsync_CommandNamingAnOwner_CarriesTheSessionUnchanged()
    {
        var envelope = await new Owned(OwnerTenant, OwnerUser, "x").MapToAsync(Session, new RecordingSerializer(), TestContext.Current.CancellationToken);

        Assert.Equal(Session, JsonSerializer.Deserialize<SessionContext>(envelope.SessionContextJson));
    }

    [Fact]
    public async Task MapToAsync_CommandNamingAnEmptyTenant_Throws()
    {
        var serializer = new RecordingSerializer();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new Owned(Guid.Empty, null, "x").MapToAsync(Session, serializer, TestContext.Current.CancellationToken));

        Assert.Empty(serializer.WrittenFor);
    }

    private sealed class RecordingSerializer : ISecureJsonSerializer
    {
        public List<(Guid? TenantId, Guid? UserId)> WrittenFor { get; } = [];

        public Task<string> SerializeAsync<T>(T obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default) =>
            SerializeAsync((object)obj!, tenantId, userId, cancellationToken);

        public Task<string> SerializeAsync(object obj, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default)
        {
            WrittenFor.Add((tenantId, userId));
            return Task.FromResult(JsonSerializer.Serialize(obj, obj.GetType()));
        }

        public Task<T?> DeserializeAsync<T>(string json, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<T>(json));

        public Task<object?> DeserializeAsync(string json, Type returnType, Guid? tenantId = null, Guid? userId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize(json, returnType));
    }
}
