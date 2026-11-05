using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// A stored encrypted value whose key was revoked or erased reads as absent and is logged, so a
/// destroyed key does not make the rest of its scope unreadable while its rows remain. A value that
/// fails authentication, is malformed or is not ciphertext at all still fails the read, as does
/// cancellation and any other failure.
/// </para>
/// <para>
/// Plaintext definitions pass through untouched. Because encrypted values share the key store's
/// scopes, <c>IKeyStore.EraseScopeAsync</c> crypto-shreds a user's encrypted settings even
/// before the rows are swept.
/// </para>
/// </remarks>
internal sealed class EncryptingSettingStore(
    ISettingStore inner,
    SettingCatalog catalog,
    ISecureBlobEncryptor encryptor,
    ILogger<EncryptingSettingStore>? logger = null) : ISettingStore
{
    private const string PurposePrefix = "stratara:setting:";

    private readonly ILogger _logger = logger ?? NullLogger<EncryptingSettingStore>.Instance;

    public async Task<string?> GetOrNullAsync(
        string name, SettingScope scope, CancellationToken cancellationToken = default)
    {
        var stored = await inner.GetOrNullAsync(name, scope, cancellationToken);
        if (stored is null || !IsEncrypted(name))
        {
            return stored;
        }

        return await TryDecryptAsync(name, stored, scope, cancellationToken);
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
            if (!IsEncrypted(name))
            {
                result[name] = value;
                continue;
            }

            if (await TryDecryptAsync(name, value, scope, cancellationToken) is { } plaintext)
            {
                result[name] = plaintext;
            }
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

    private async Task<string?> TryDecryptAsync(
        string name, string stored, SettingScope scope, CancellationToken cancellationToken)
    {
        var ciphertext = Convert.FromBase64String(stored);
        try
        {
            return await DecryptAsync(name, ciphertext, scope, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            SettingStoreLog.EncryptedSettingKeyUnavailable(_logger, name, scope.NormalizedTenantId, scope.NormalizedUserId);
            return null;
        }
    }

    private async Task<string> DecryptAsync(
        string name, byte[] ciphertext, SettingScope scope, CancellationToken cancellationToken)
    {
        using var encryptedStream = new MemoryStream(ciphertext);
        await using var decrypted = await encryptor.DecryptAsync(
            encryptedStream, ToKeyScope(scope), PurposePrefix + name, cancellationToken);

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
