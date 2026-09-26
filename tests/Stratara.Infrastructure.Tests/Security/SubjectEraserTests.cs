using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Erasure;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;
using Stratara.Infrastructure.Security;
using Stratara.Testing;
using Xunit;

namespace Stratara.Infrastructure.Tests.Security;

public class SubjectEraserTests
{
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly Guid OtherUser = Guid.CreateVersion7();
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();

    private sealed class Fixture
    {
        public InMemoryTenantMembershipStore Memberships { get; } = new();
        public InMemorySettingStore Settings { get; } = new();
        public InMemoryKeyStore Keys { get; } = new();
        public InMemoryApiKeyStore ApiKeys { get; }
        public ISettingStore SettingStoreOverride { get; set; }

        public Fixture()
        {
            ApiKeys = new InMemoryApiKeyStore(Memberships);
            SettingStoreOverride = Settings;
        }

        public SubjectEraser Build() => new(Memberships, ApiKeys, SettingStoreOverride, Keys);
    }

    private static async Task SeedAsync(Fixture f)
    {
        await f.Memberships.SetMembershipAsync(new TenantMembership(User, TenantA, ["admin"]));
        await f.Memberships.SetMembershipAsync(new TenantMembership(User, TenantB, ["reader"]));
        await f.Memberships.SetMembershipAsync(new TenantMembership(OtherUser, TenantA, ["reader"]));
        await f.Memberships.SetActiveTenantAsync(User, TenantA);

        await f.Settings.SetAsync("theme", "dark", SettingScope.ForUser(User));
        await f.Settings.SetAsync("locale", "de", SettingScope.ForUserInTenant(TenantA, User));
        await f.Settings.SetAsync("locale", "en", SettingScope.ForUserInTenant(TenantB, User));
        await f.Settings.SetAsync("locale", "fr", SettingScope.ForUserInTenant(TenantA, OtherUser));
        await f.Settings.SetAsync("brand", "acme", SettingScope.ForTenant(TenantA));

        await f.Keys.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.UserScoped, null, User.ToString("D")));
        await f.Keys.GetOrCreateCurrentKeyAsync(
            new KeyScope(DataSensitivityLevel.UserScoped, TenantA.ToString("D"), User.ToString("D")));
        await f.Keys.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.TenantScoped, TenantA.ToString("D")));

        await f.ApiKeys.IssueAsync(new ApiKeyIssueRequest(TenantA, "user-key", [], User));
        await f.ApiKeys.IssueAsync(new ApiKeyIssueRequest(TenantA, "other-key", [], OtherUser));
    }

    [Fact]
    public async Task EraseUser_SweepsEveryPlane_InOrder()
    {
        var f = new Fixture();
        await SeedAsync(f);

        var report = await f.Build().EraseUserAsync(User);

        Assert.Equal(
            [ErasurePlane.ApiKeys, ErasurePlane.Settings, ErasurePlane.KeyMaterial, ErasurePlane.Memberships],
            report.Planes.Select(p => p.Plane));

        Assert.Empty(await f.Memberships.GetMembershipsAsync(User));
        Assert.Null(await f.Memberships.GetActiveTenantAsync(User));
        Assert.Empty(await f.Settings.GetAllAsync(SettingScope.ForUser(User)));
        Assert.Empty(await f.Settings.GetAllAsync(SettingScope.ForUserInTenant(TenantA, User)));
        Assert.Empty(await f.Settings.GetAllAsync(SettingScope.ForUserInTenant(TenantB, User)));
        Assert.DoesNotContain(await f.ApiKeys.GetForTenantAsync(TenantA), k => k.UserId == User);
    }

    [Fact]
    public async Task EraseUser_LeavesOtherSubjectsAlone()
    {
        var f = new Fixture();
        await SeedAsync(f);

        await f.Build().EraseUserAsync(User);

        Assert.Single(await f.Memberships.GetMembershipsAsync(OtherUser));
        Assert.NotEmpty(await f.Settings.GetAllAsync(SettingScope.ForUserInTenant(TenantA, OtherUser)));
        Assert.NotEmpty(await f.Settings.GetAllAsync(SettingScope.ForTenant(TenantA)));
        Assert.Single(await f.ApiKeys.GetForTenantAsync(TenantA), k => k.UserId == OtherUser);
    }

    [Fact]
    public async Task EraseUser_CoversEveryTenantTheUserBelongsTo()
    {
        var f = new Fixture();
        await SeedAsync(f);

        var report = await f.Build().EraseUserAsync(User);

        var settingScopes = report.Planes.Single(p => p.Plane == ErasurePlane.Settings).Scopes;
        Assert.Equal(3, settingScopes.Count);

        var keyScopes = report.Planes.Single(p => p.Plane == ErasurePlane.KeyMaterial).Scopes;
        string[] expected =
        [
            $"key scope UserScoped - / {User:D}",
            $"key scope UserScoped {TenantA:D} / {User:D}",
            $"key scope UserScoped {TenantB:D} / {User:D}",
        ];
        Assert.Equal(expected.Order(), keyScopes.Order());
    }

    [Fact]
    public async Task EraseTenant_SweepsEveryPlane()
    {
        var f = new Fixture();
        await SeedAsync(f);

        var report = await f.Build().EraseTenantAsync(TenantA);

        Assert.Equal(
            [ErasurePlane.ApiKeys, ErasurePlane.Settings, ErasurePlane.KeyMaterial, ErasurePlane.Memberships],
            report.Planes.Select(p => p.Plane));

        Assert.Empty(await f.Memberships.GetMembersAsync(TenantA));
        Assert.Empty(await f.Settings.GetAllAsync(SettingScope.ForTenant(TenantA)));
        Assert.Empty(await f.Settings.GetAllAsync(SettingScope.ForUserInTenant(TenantA, User)));
        Assert.Empty(await f.ApiKeys.GetForTenantAsync(TenantA));

        Assert.Single(await f.Memberships.GetMembershipsAsync(User));
        Assert.NotEmpty(await f.Settings.GetAllAsync(SettingScope.ForUserInTenant(TenantB, User)));
    }

    [Fact]
    public async Task APlaneFailing_StopsTheErasure_AndNamesThePlane()
    {
        var f = new Fixture();
        await SeedAsync(f);
        f.SettingStoreOverride = new ThrowingSettingStore(f.Settings);

        var ex = await Assert.ThrowsAsync<ErasureIncompleteException>(() => f.Build().EraseUserAsync(User));

        Assert.Equal(ErasurePlane.Settings, ex.Plane);
        Assert.Equal([ErasurePlane.ApiKeys], ex.Completed.Planes.Select(p => p.Plane));
    }

    [Fact]
    public async Task APlaneFailing_LeavesKeyMaterialIntact()
    {
        var f = new Fixture();
        await SeedAsync(f);
        var userScope = new KeyScope(DataSensitivityLevel.UserScoped, null, User.ToString("D"));
        var keyId = (await f.Keys.GetOrCreateCurrentKeyAsync(userScope)).KeyId;
        f.SettingStoreOverride = new ThrowingSettingStore(f.Settings);

        await Assert.ThrowsAsync<ErasureIncompleteException>(() => f.Build().EraseUserAsync(User));

        Assert.NotNull(await f.Keys.GetDataEncryptionKeyAsync(keyId));
        Assert.NotEmpty(await f.Memberships.GetMembershipsAsync(User));
    }

    /// <summary>
    /// Memberships are what name the tenants whose keys a user's erasure shreds, so they go after the
    /// key material: a run repeated after the key sweep failed still finds them and still shreds the
    /// keys the first run did not reach.
    /// </summary>
    [Fact]
    public async Task AKeySweepFailing_LeavesTheMembershipsASecondRunNeeds()
    {
        var f = new Fixture();
        await SeedAsync(f);
        var tenantScope = new KeyScope(DataSensitivityLevel.UserScoped, TenantA.ToString("D"), User.ToString("D"));
        var keyId = (await f.Keys.GetOrCreateCurrentKeyAsync(tenantScope)).KeyId;
        var failingOnce = new FailingOnceKeyStore(f.Keys);

        var ex = await Assert.ThrowsAsync<ErasureIncompleteException>(() =>
            new SubjectEraser(f.Memberships, f.ApiKeys, f.Settings, failingOnce).EraseUserAsync(User));
        Assert.Equal(ErasurePlane.KeyMaterial, ex.Plane);
        Assert.NotEmpty(await f.Memberships.GetMembershipsAsync(User));

        await new SubjectEraser(f.Memberships, f.ApiKeys, f.Settings, failingOnce).EraseUserAsync(User);

        Assert.Null(await f.Keys.GetDataEncryptionKeyAsync(keyId));
        Assert.Empty(await f.Memberships.GetMembershipsAsync(User));
    }

    private sealed class FailingOnceKeyStore(IKeyStore inner) : IKeyStore
    {
        private bool _failed;

        public ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
            => inner.GetOrCreateCurrentKeyAsync(scope, cancellationToken);

        public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.GetDataEncryptionKeyAsync(keyId, cancellationToken);

        public ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default)
            => inner.RotateAsync(scope, cancellationToken);

        public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
            => inner.RevokeAsync(keyId, cancellationToken);

        public ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default)
        {
            if (_failed)
            {
                return inner.EraseScopeAsync(scope, cancellationToken);
            }

            _failed = true;
            throw new InvalidOperationException("the key store is down");
        }
    }

    private sealed class ThrowingSettingStore(ISettingStore inner) : ISettingStore
    {
        public Task<string?> GetOrNullAsync(string name, SettingScope scope, CancellationToken cancellationToken = default)
            => inner.GetOrNullAsync(name, scope, cancellationToken);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(SettingScope scope, CancellationToken cancellationToken = default)
            => inner.GetAllAsync(scope, cancellationToken);

        public Task SetAsync(string name, string? value, SettingScope scope, CancellationToken cancellationToken = default)
            => inner.SetAsync(name, value, scope, cancellationToken);

        public Task DeleteScopeAsync(SettingScope scope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the setting store is down");
    }
}
