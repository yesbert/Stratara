using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Catalog-aware decorator over an <see cref="ISettingStore"/> that transparently encrypts the
/// values of definitions declared with <c>IsEncrypted = true</c> — AES-GCM via the security
/// plane's <see cref="ISecureBlobEncryptor"/>, key scope derived from the setting scope
/// (user → user-scoped key, tenant → tenant-scoped key, global → confidential key) and the
/// purpose bound to the setting name, so a leaked row cannot be decrypted against another
/// scope's key or replayed under a different setting.
/// </summary>
/// <remarks>
/// Plaintext definitions pass through untouched. Because encrypted values share the key store's
/// scopes, <c>IKeyStore.EraseScopeAsync</c> crypto-shreds a user's encrypted settings even
/// before the rows are swept.
/// </remarks>
internal sealed class EncryptingSettingStore(
    ISettingStore inner,
    SettingCatalog catalog,
    ISecureBlobEncryptor encryptor) : ISettingStore
{
    private const string PurposePrefix = "stratara:setting:";

    public async Task<string?> GetOrNullAsync(
        string name, SettingScope scope, CancellationToken cancellationToken = default)
    {
        var stored = await inner.GetOrNullAsync(name, scope, cancellationToken);
        if (stored is null || !IsEncrypted(name))
        {
            return stored;
        }

        return await DecryptAsync(stored, scope, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        SettingScope scope, CancellationToken cancellationToken = default)
    {
        var stored = await inner.GetAllAsync(scope, cancellationToken);
        if (stored.Count == 0)
        {
            return stored;
        }

        var result = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var (name, value) in stored)
        {
            result[name] = IsEncrypted(name)
                ? await DecryptAsync(value, scope, cancellationToken)
                : value;
        }

        return result;
    }

    public async Task SetAsync(
        string name, string? value, SettingScope scope, CancellationToken cancellationToken = default)
    {
        if (value is not null && IsEncrypted(name))
        {
            value = await EncryptAsync(name, value, scope, cancellationToken);
        }

        await inner.SetAsync(name, value, scope, cancellationToken);
    }

    public Task DeleteScopeAsync(SettingScope scope, CancellationToken cancellationToken = default) =>
        inner.DeleteScopeAsync(scope, cancellationToken);

    private bool IsEncrypted(string name) => catalog.GetOrNull(name) is { IsEncrypted: true };

    private async Task<string> EncryptAsync(
        string name, string plaintext, SettingScope scope, CancellationToken cancellationToken)
    {
        using var plainStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(plaintext));
        await using var encrypted = await encryptor.EncryptAsync(
            plainStream, ToKeyScope(scope), PurposePrefix + name, cancellationToken);

        using var buffer = new MemoryStream();
        await encrypted.CopyToAsync(buffer, cancellationToken);
        return Convert.ToBase64String(buffer.ToArray());
    }

    private async Task<string> DecryptAsync(
        string stored, SettingScope scope, CancellationToken cancellationToken)
    {
        using var encryptedStream = new MemoryStream(Convert.FromBase64String(stored));
        await using var decrypted = await encryptor.DecryptAsync(
            encryptedStream, ToKeyScope(scope), cancellationToken);

        using var buffer = new MemoryStream();
        await decrypted.CopyToAsync(buffer, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static KeyScope ToKeyScope(SettingScope scope) => scope switch
    {
        { UserId: not null } => new KeyScope(DataSensitivityLevel.UserScoped, scope.TenantId, scope.UserId),
        { TenantId: not null } => new KeyScope(DataSensitivityLevel.TenantScoped, scope.TenantId),
        _ => new KeyScope(DataSensitivityLevel.Confidential),
    };
}
