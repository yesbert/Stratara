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
}
