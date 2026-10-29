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

    /// <summary>A confidential value written for a tenant has a key naming that tenant, like any other level.</summary>
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

    /// <summary>
    /// A person's own data is written under the reserved default tenant, so it names no tenant of theirs. The directory
    /// has to name that scope itself: a key store that cannot list would otherwise leave it behind.
    /// </summary>
    [Fact]
    public async Task A_users_erasure_reaches_their_own_data_through_a_key_store_that_cannot_list()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var ownValue = await f.WriteAsync(new DefaultLevelNote { Text = "profile" }, Guid.Empty, Member);
        Assert.Equal("profile", await f.ReadAsync<DefaultLevelNote>(ownValue, Guid.Empty, Member));

        await f.Eraser(new NonListingKeyStore(f.Keys), new CapturingLogger()).EraseUserAsync(Member);

        Assert.Null(await f.ReadAsync<DefaultLevelNote>(ownValue, Guid.Empty, Member));
    }

    [Fact]
    public async Task A_tenants_erasure_leaves_a_members_own_data()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var ownValue = await f.WriteAsync(new DefaultLevelNote { Text = "profile" }, Guid.Empty, Member);

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Equal("profile", await f.ReadAsync<DefaultLevelNote>(ownValue, Guid.Empty, Member));
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

    /// <summary>Work that was already running when the tenant was erased encrypts afterwards: refused, whether the key existed or not.</summary>
    [Fact]
    public async Task Work_for_an_erased_tenant_that_encrypts_afterwards_is_refused()
    {
        var f = new Fixture();
        var outsider = Guid.CreateVersion7();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        await f.WriteAsync(new TenantLevelNote { Text = "before" }, Tenant, Member);

        await f.Eraser().EraseTenantAsync(Tenant);

        await Assert.ThrowsAsync<KeyScopeErasedException>(() =>
            f.WriteAsync(new TenantLevelNote { Text = "after" }, Tenant, Member));
        await Assert.ThrowsAsync<KeyScopeErasedException>(() =>
            f.WriteAsync(new TenantLevelNote { Text = "first time" }, Tenant, outsider));
        Assert.DoesNotContain(await f.Keys.ListScopesAsync(), s => KeySubject.Tenant(Tenant.ToString("D")).Covers(s));
        Assert.NotNull(await f.WriteAsync(new TenantLevelNote { Text = "kept" }, OtherTenant, outsider));
    }

    [Fact]
    public async Task Work_for_an_erased_user_that_encrypts_afterwards_is_refused_and_their_tenant_still_writes()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));

        await f.Eraser().EraseUserAsync(Member);

        await Assert.ThrowsAsync<KeyScopeErasedException>(() =>
            f.WriteAsync(new DefaultLevelNote { Text = "after" }, OtherTenant, Member));
        Assert.NotNull(await f.WriteAsync(new TenantLevelNote { Text = "kept" }, Tenant, Member));
    }

    [Fact]
    public async Task The_subject_is_marked_before_its_keys_are_listed()
    {
        var f = new Fixture();
        var recording = new RecordingKeyStore(f.Keys);

        await f.Eraser(recording, NullLogger<SubjectEraser>.Instance).EraseTenantAsync(Tenant);

        Assert.Equal(["mark", "list"], recording.Calls.Where(c => c is "mark" or "list"));
    }

    [Fact]
    public async Task An_erasure_run_again_marks_again_without_failing()
    {
        var f = new Fixture();

        await f.Eraser().EraseTenantAsync(Tenant);
        var report = await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Contains(ErasurePlane.KeyMaterial, report.Planes.Select(p => p.Plane));
    }

    /// <summary>A key store of the consumer's own that lists but cannot mark: keys are shredded, and the erasure says they can come back.</summary>
    [Fact]
    public async Task A_key_store_that_cannot_mark_still_shreds_and_warns()
    {
        var f = new Fixture();
        var logger = new CapturingLogger();
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);

        await f.Eraser(new NonMarkingKeyStore(f.Keys), logger).EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));
        Assert.Contains(LogEvents.KeyManagement.SubjectNotMarkable, logger.EventIds);
        Assert.DoesNotContain(LogEvents.KeyManagement.KeyScopesNotListable, logger.EventIds);
    }

    /// <summary>The mark comes before every plane, so no encrypted setting is written between the settings sweep and the shred.</summary>
    [Fact]
    public async Task A_mark_that_fails_stops_the_erasure_before_any_plane_is_swept()
    {
        var f = new Fixture();

        var failure = await Assert.ThrowsAsync<ErasureIncompleteException>(() =>
            f.Eraser(new FailingMarkKeyStore(f.Keys), NullLogger<SubjectEraser>.Instance).EraseTenantAsync(Tenant));

        Assert.Equal(ErasurePlane.KeyMaterial, failure.Plane);
        Assert.Empty(failure.Completed.Planes);
    }

    private class NonMarkingKeyStore(IKeyStore inner) : NonListingKeyStore(inner), IKeyStore
    {
        public ValueTask<IReadOnlyList<KeyScope>> ListScopesAsync(CancellationToken cancellationToken = default)
            => Inner.ListScopesAsync(cancellationToken);
    }

    private sealed class FailingMarkKeyStore(IKeyStore inner) : NonMarkingKeyStore(inner), IKeyStore
    {
        public ValueTask MarkSubjectErasedAsync(KeySubject subject, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the key store is down");
    }

    private sealed class RecordingKeyStore(IKeyStore inner) : NonMarkingKeyStore(inner), IKeyStore
    {
        public List<string> Calls { get; } = [];

        public ValueTask MarkSubjectErasedAsync(KeySubject subject, CancellationToken cancellationToken = default)
        {
            Calls.Add("mark");
            return Inner.MarkSubjectErasedAsync(subject, cancellationToken);
        }

        public new ValueTask<IReadOnlyList<KeyScope>> ListScopesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("list");
            return Inner.ListScopesAsync(cancellationToken);
        }
    }

    private class NonListingKeyStore(IKeyStore inner) : IKeyStore
    {
        protected IKeyStore Inner => inner;

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
