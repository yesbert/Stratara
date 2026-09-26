using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.Erasure;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Diagnostics;
using Stratara.Infrastructure.Security;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Testing;
using Xunit;

namespace Stratara.Infrastructure.Tests.Security;

/// <summary>
/// <c>tenant-directory</c> → a subject's erasure removes its key material, so data encrypted under its keys becomes
/// unrecoverable. The serializer names a key by level, tenant and user together, and the level decides whose erasure
/// a value dies with: every level is the tenant's, the user level alone is the user's. Each test encrypts a value the
/// way an event's payload is encrypted, reads it back once as a control, erases a subject, and reads it again.
/// </summary>
public class SubjectEraserKeyCoverageTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    private sealed class DefaultLevelNote
    {
        [EncryptData]
        public string? Text { get; set; }
    }

    private sealed class TenantLevelNote
    {
        [EncryptData(DataSensitivityLevel.TenantScoped)]
        public string? Text { get; set; }
    }

    private sealed class ConfidentialNote
    {
        [EncryptData(DataSensitivityLevel.Confidential)]
        public string? Text { get; set; }
    }

    private sealed class Fixture
    {
        public InMemoryTenantMembershipStore Memberships { get; } = new();
        public InMemoryKeyStore Keys { get; } = new();

        public SecureJsonSerializer Serializer { get; }

        public Fixture()
        {
            var encryption = new ServiceCollection().AddStrataraBlobEncryption().BuildServiceProvider()
                .GetRequiredService<IEncryptionFactory>();
            Serializer = new SecureJsonSerializer(Keys, encryption, NullLogger<SecureJsonSerializer>.Instance, new ProductionEnvironment());
        }

        public SubjectEraser Eraser() =>
            new(Memberships, new InMemoryApiKeyStore(Memberships), new InMemorySettingStore(), Keys);

        public SubjectEraser Eraser(IKeyStore keys, ILogger<SubjectEraser> logger) =>
            new(Memberships, new InMemoryApiKeyStore(Memberships), new InMemorySettingStore(), keys, logger);

        public async Task<string> WriteAsync<T>(T note, Guid tenantId, Guid? userId) =>
            await Serializer.SerializeAsync(note, tenantId, userId);

        public async Task<string?> ReadAsync<T>(string json, Guid tenantId, Guid? userId) where T : class =>
            (await Serializer.DeserializeAsync<T>(json, tenantId, userId)) switch
            {
                DefaultLevelNote n => n.Text,
                TenantLevelNote n => n.Text,
                ConfidentialNote n => n.Text,
                _ => null
            };
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task A_tenants_erasure_reaches_a_default_level_value_written_with_no_user()
    {
        var f = new Fixture();
        var json = await f.WriteAsync(new DefaultLevelNote { Text = "secret" }, Tenant, null);
        var elsewhere = await f.WriteAsync(new DefaultLevelNote { Text = "kept" }, OtherTenant, null);
        Assert.Equal("secret", await f.ReadAsync<DefaultLevelNote>(json, Tenant, null));

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<DefaultLevelNote>(json, Tenant, null));
        Assert.Equal("kept", await f.ReadAsync<DefaultLevelNote>(elsewhere, OtherTenant, null));
    }

    [Fact]
    public async Task A_tenants_erasure_reaches_a_tenant_level_value_written_for_a_member()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);
        Assert.Equal("secret", await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));
    }

    /// <summary>The serializer binds every value to a tenant, the confidential level included.</summary>
    [Fact]
    public async Task A_tenants_erasure_reaches_a_confidential_value_written_for_it()
    {
        var f = new Fixture();
        var json = await f.WriteAsync(new ConfidentialNote { Text = "secret" }, Tenant, null);
        var elsewhere = await f.WriteAsync(new ConfidentialNote { Text = "kept" }, OtherTenant, null);
        Assert.Equal("secret", await f.ReadAsync<ConfidentialNote>(json, Tenant, null));

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<ConfidentialNote>(json, Tenant, null));
        Assert.Equal("kept", await f.ReadAsync<ConfidentialNote>(elsewhere, OtherTenant, null));
    }

    [Fact]
    public async Task A_users_erasure_reaches_their_user_level_values_and_leaves_tenant_level_ones_to_the_tenant()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var userLevel = await f.WriteAsync(new DefaultLevelNote { Text = "secret" }, Tenant, Member);
        var tenantLevel = await f.WriteAsync(new TenantLevelNote { Text = "kept" }, Tenant, Member);
        Assert.Equal("secret", await f.ReadAsync<DefaultLevelNote>(userLevel, Tenant, Member));

        await f.Eraser().EraseUserAsync(Member);

        Assert.Null(await f.ReadAsync<DefaultLevelNote>(userLevel, Tenant, Member));
        Assert.Equal("kept", await f.ReadAsync<TenantLevelNote>(tenantLevel, Tenant, Member));
    }

    /// <summary>A platform operator acting in a tenant is no member of it; the key it wrote under is still the tenant's.</summary>
    [Fact]
    public async Task A_tenants_erasure_reaches_a_value_written_for_a_user_who_is_no_member()
    {
        var f = new Fixture();
        var outsider = Guid.CreateVersion7();
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, outsider);
        var elsewhere = await f.WriteAsync(new TenantLevelNote { Text = "kept" }, OtherTenant, outsider);
        Assert.Equal("secret", await f.ReadAsync<TenantLevelNote>(json, Tenant, outsider));

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, outsider));
        Assert.Equal("kept", await f.ReadAsync<TenantLevelNote>(elsewhere, OtherTenant, outsider));
    }

    [Fact]
    public async Task A_users_erasure_reaches_their_value_in_a_tenant_they_have_left()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var someoneElse = Guid.CreateVersion7();
        var json = await f.WriteAsync(new DefaultLevelNote { Text = "secret" }, OtherTenant, Member);
        var someoneElses = await f.WriteAsync(new DefaultLevelNote { Text = "kept" }, OtherTenant, someoneElse);
        Assert.Equal("secret", await f.ReadAsync<DefaultLevelNote>(json, OtherTenant, Member));

        await f.Eraser().EraseUserAsync(Member);

        Assert.Null(await f.ReadAsync<DefaultLevelNote>(json, OtherTenant, Member));
        Assert.Equal("kept", await f.ReadAsync<DefaultLevelNote>(someoneElses, OtherTenant, someoneElse));
    }

    /// <summary>An erasure before 4.4.0 removed the memberships and left keys it shared with members; running it again reaches them.</summary>
    [Fact]
    public async Task A_tenants_erasure_run_again_reaches_what_an_earlier_run_left()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);
        await f.Memberships.RemoveAllMembersAsync(Tenant);

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));
    }

    [Fact]
    public async Task A_tenants_erasure_leaves_a_confidential_value_written_with_no_tenant()
    {
        var f = new Fixture();
        var systemWide = await f.WriteAsync(new ConfidentialNote { Text = "kept" }, Guid.Empty, Guid.Empty);

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Equal("kept", await f.ReadAsync<ConfidentialNote>(systemWide, Guid.Empty, Guid.Empty));
    }

    [Fact]
    public async Task An_empty_id_is_refused_rather_than_erasing_what_it_keys()
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Eraser().EraseTenantAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Eraser().EraseUserAsync(Guid.Empty));
    }

    /// <summary>A key store of the consumer's own that does not list its scopes: the erasure still runs, and says what it could not reach.</summary>
    [Fact]
    public async Task A_key_store_that_cannot_list_falls_back_to_the_directory_and_warns()
    {
        var f = new Fixture();
        var logger = new CapturingLogger();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var memberValue = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);

        await f.Eraser(new NonListingKeyStore(f.Keys), logger).EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(memberValue, Tenant, Member));
        Assert.Contains(LogEvents.KeyManagement.KeyScopesNotListable, logger.EventIds);
    }

    [Fact]
    public async Task A_listing_that_fails_stops_the_erasure_at_the_key_material()
    {
        var f = new Fixture();

        var failure = await Assert.ThrowsAsync<ErasureIncompleteException>(() =>
            f.Eraser(new FailingListingKeyStore(f.Keys), NullLogger<SubjectEraser>.Instance).EraseTenantAsync(Tenant));

        Assert.Equal(ErasurePlane.KeyMaterial, failure.Plane);
        Assert.Equal([ErasurePlane.ApiKeys, ErasurePlane.Settings], failure.Completed.Planes.Select(p => p.Plane));
    }

    private class NonListingKeyStore(IKeyStore inner) : IKeyStore
    {
        public ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
            => inner.GetOrCreateCurrentKeyAsync(scope, cancellationToken);

        public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.GetDataEncryptionKeyAsync(keyId, cancellationToken);

        public ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default)
            => inner.RotateAsync(scope, cancellationToken);

        public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.RevokeAsync(keyId, cancellationToken);

        public ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default)
            => inner.EraseScopeAsync(scope, cancellationToken);
    }

    private sealed class FailingListingKeyStore(IKeyStore inner) : NonListingKeyStore(inner), IKeyStore
    {
        public ValueTask<IReadOnlyList<KeyScope>> ListScopesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the key store is down");
    }

    private sealed class CapturingLogger : ILogger<SubjectEraser>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => EventIds.Add(eventId.Id);
    }
}
