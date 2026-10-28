using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Testing.Tests;

public class InMemoryKeyStoreTests
{
    private static KeyScope Scope(Guid tenant) => new(DataSensitivityLevel.TenantScoped, tenant.ToString());

    [Fact]
    public async Task GetOrCreate_is_stable_for_the_same_scope()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var first = await store.GetOrCreateCurrentKeyAsync(scope);
        var second = await store.GetOrCreateCurrentKeyAsync(scope);

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(first.Key.ToArray(), second.Key.ToArray());
        Assert.Equal(32, first.Key.Length);
    }

    [Fact]
    public async Task Different_scopes_get_different_keys()
    {
        var store = new InMemoryKeyStore();

        var a = await store.GetOrCreateCurrentKeyAsync(Scope(Guid.CreateVersion7()));
        var b = await store.GetOrCreateCurrentKeyAsync(Scope(Guid.CreateVersion7()));

        Assert.NotEqual(a.KeyId, b.KeyId);
        Assert.NotEqual(a.Key.ToArray(), b.Key.ToArray());
    }

    [Fact]
    public async Task Returned_key_buffer_is_a_copy()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var material = await store.GetOrCreateCurrentKeyAsync(scope);
        material.Key.ToArray().AsSpan().Clear(); // caller mutating its copy must not corrupt the store

        var again = await store.GetDataEncryptionKeyAsync(material.KeyId);
        Assert.NotNull(again);
        Assert.Contains(again!, b => b != 0);
    }

    [Fact]
    public async Task Rotate_makes_a_new_current_but_keeps_old_resolvable()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var original = await store.GetOrCreateCurrentKeyAsync(scope);
        var rotatedId = await store.RotateAsync(scope);
        var current = await store.GetOrCreateCurrentKeyAsync(scope);

        Assert.NotEqual(original.KeyId, rotatedId);
        Assert.Equal(rotatedId, current.KeyId);
        Assert.NotNull(await store.GetDataEncryptionKeyAsync(original.KeyId)); // old ciphertext still decryptable
    }

    [Fact]
    public async Task Revoke_shreds_a_single_version()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var material = await store.GetOrCreateCurrentKeyAsync(scope);
        await store.RevokeAsync(material.KeyId);

        Assert.Null(await store.GetDataEncryptionKeyAsync(material.KeyId));
    }

    [Fact]
    public async Task Revoking_the_current_version_leaves_the_scope_usable()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var revoked = await store.GetOrCreateCurrentKeyAsync(scope);
        await store.RevokeAsync(revoked.KeyId);
        var next = await store.GetOrCreateCurrentKeyAsync(scope);

        Assert.NotEqual(revoked.KeyId, next.KeyId);
        Assert.NotNull(await store.GetDataEncryptionKeyAsync(next.KeyId));
    }

    [Fact]
    public async Task Revoking_the_newest_version_falls_back_to_the_highest_remaining_one()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var first = await store.GetOrCreateCurrentKeyAsync(scope);
        var rotated = await store.RotateAsync(scope);
        await store.RevokeAsync(rotated);

        Assert.Equal(first.KeyId, (await store.GetOrCreateCurrentKeyAsync(scope)).KeyId);
    }

    [Fact]
    public async Task EraseScope_shreds_every_version()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());

        var v1 = await store.GetOrCreateCurrentKeyAsync(scope);
        var v2Id = await store.RotateAsync(scope);

        await store.EraseScopeAsync(scope);

        Assert.Null(await store.GetDataEncryptionKeyAsync(v1.KeyId));
        Assert.Null(await store.GetDataEncryptionKeyAsync(v2Id));
    }

    [Fact]
    public async Task ListScopes_returns_every_scope_holding_a_key_and_none_erased()
    {
        var store = new InMemoryKeyStore();
        var tenant = Guid.NewGuid().ToString("D");
        var user = Guid.NewGuid().ToString("D");
        KeyScope[] kept =
        [
            new(DataSensitivityLevel.UserScoped, tenant, user),
            new(DataSensitivityLevel.UserScoped, tenant),
            new(DataSensitivityLevel.TenantScoped, tenant, user),
            new(DataSensitivityLevel.UserScoped, null, user),
        ];
        var erased = new KeyScope(DataSensitivityLevel.Confidential, tenant);
        foreach (var scope in kept.Append(erased))
        {
            await store.GetOrCreateCurrentKeyAsync(scope);
        }

        await store.EraseScopeAsync(erased);

        Assert.Equal(kept.OrderBy(s => s.ToString()), (await store.ListScopesAsync()).OrderBy(s => s.ToString()));
    }

    [Fact]
    public async Task A_marked_tenant_is_refused_at_every_level_and_nothing_is_created()
    {
        var store = new InMemoryKeyStore();
        var tenant = Guid.CreateVersion7();
        var held = Scope(tenant);
        await store.GetOrCreateCurrentKeyAsync(held);
        await store.EraseScopeAsync(held);

        await store.MarkSubjectErasedAsync(KeySubject.Tenant(tenant.ToString()));

        var refused = await Assert.ThrowsAsync<KeyScopeErasedException>(async () => await store.GetOrCreateCurrentKeyAsync(held));
        Assert.Equal(held, refused.Scope);
        await Assert.ThrowsAsync<KeyScopeErasedException>(async () => await store.RotateAsync(held));
        foreach (var level in Enum.GetValues<DataSensitivityLevel>())
        {
            await Assert.ThrowsAsync<KeyScopeErasedException>(async () =>
                await store.GetOrCreateCurrentKeyAsync(new KeyScope(level, tenant.ToString().ToUpperInvariant(), Guid.CreateVersion7().ToString())));
        }

        Assert.Empty(await store.ListScopesAsync());
    }

    [Fact]
    public async Task A_marked_user_is_refused_at_the_user_level_only_and_another_subject_is_unaffected()
    {
        var store = new InMemoryKeyStore();
        var tenant = Guid.CreateVersion7().ToString();
        var user = Guid.CreateVersion7().ToString();

        await store.MarkSubjectErasedAsync(KeySubject.User(user));
        await store.MarkSubjectErasedAsync(KeySubject.User(user));

        await Assert.ThrowsAsync<KeyScopeErasedException>(async () =>
            await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.UserScoped, tenant, user)));
        Assert.NotNull(await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.TenantScoped, tenant, user)));
        var other = Scope(Guid.CreateVersion7());
        Assert.NotNull(await store.GetOrCreateCurrentKeyAsync(other));
        Assert.NotNull(await store.RotateAsync(other));
    }

    [Fact]
    public async Task The_default_subject_is_refused()
    {
        var store = new InMemoryKeyStore();

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.MarkSubjectErasedAsync(default));

        Assert.NotNull(await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.Confidential)));
    }

    [Fact]
    public async Task Revoking_every_version_is_not_an_erasure()
    {
        var store = new InMemoryKeyStore();
        var scope = Scope(Guid.CreateVersion7());
        var v1 = await store.GetOrCreateCurrentKeyAsync(scope);
        var v2 = await store.RotateAsync(scope);

        await store.RevokeAsync(v1.KeyId);
        await store.RevokeAsync(v2);

        Assert.NotEqual(v2, (await store.GetOrCreateCurrentKeyAsync(scope)).KeyId);
    }
}
