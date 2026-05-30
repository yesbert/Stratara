using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Security;
using Stratara.Diagnostics;

namespace Stratara.Security;

/// <summary>
/// Production <see cref="IKeyStore"/> that stores versioned per-scope data-encryption keys (DEKs)
/// as KEK-wrapped blobs in a single JSON file. The master KEK comes from
/// <see cref="IMasterKeyProvider"/>; the store file never holds a plaintext key.
/// </summary>
/// <remarks>
/// Each DEK is 32 random bytes wrapped with AES-256-GCM under the KEK, with the key id as
/// associated data so a wrapped DEK cannot be moved to a different key id / scope. DEKs are
/// unwrapped only transiently in memory and zeroed after use. <see cref="RevokeAsync"/> marks a
/// version unusable; <see cref="EraseScopeAsync"/> deletes every wrapped DEK for a scope, making
/// its ciphertext permanently undecryptable (GDPR Art. 17 crypto-shred).
/// </remarks>
internal sealed partial class EnvelopeFileKeyStore : IKeyStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IMasterKeyProvider _masterKeyProvider;
    private readonly ILogger<EnvelopeFileKeyStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KeyStoreFile _state;

    public EnvelopeFileKeyStore(
        IMasterKeyProvider masterKeyProvider,
        IOptions<StrataraFileKeyStoreOptions> options,
        ILogger<EnvelopeFileKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _masterKeyProvider = masterKeyProvider;
        _logger = logger;
        _filePath = Path.GetFullPath(options.Value.StorePath);
        _state = LoadFromDisk(_filePath);
    }

    /// <inheritdoc/>
    public async ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        var scopeKey = BuildScopeKey(scope);
        var kek = await _masterKeyProvider.GetMasterKeyAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentKeyId = HighestNonRevokedKeyId(scopeKey);
            if (currentKeyId is null)
            {
                currentKeyId = CreateKeyUnlocked(scopeKey, kek.Span);
                await PersistUnlockedAsync(cancellationToken);
                LogKeyCreated(_logger, currentKeyId);
            }

            var wrapped = _state.Scopes[scopeKey].Keys[currentKeyId];
            var dek = Unwrap(wrapped, currentKeyId, kek.Span);
            return new KeyMaterial(currentKeyId, dek);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var kek = await _masterKeyProvider.GetMasterKeyAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var scope in _state.Scopes.Values)
            {
                if (scope.Keys.TryGetValue(keyId, out var wrapped))
                {
                    return wrapped.Revoked ? null : Unwrap(wrapped, keyId, kek.Span);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        var scopeKey = BuildScopeKey(scope);
        var kek = await _masterKeyProvider.GetMasterKeyAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keyId = CreateKeyUnlocked(scopeKey, kek.Span);
            await PersistUnlockedAsync(cancellationToken);
            LogKeyRotated(_logger, keyId);
            return keyId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var scope in _state.Scopes.Values)
            {
                if (scope.Keys.TryGetValue(keyId, out var wrapped))
                {
                    if (!wrapped.Revoked)
                    {
                        wrapped.Revoked = true;
                        await PersistUnlockedAsync(cancellationToken);
                        LogKeyRevoked(_logger, keyId);
                    }

                    return;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        var scopeKey = BuildScopeKey(scope);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.Scopes.Remove(scopeKey))
            {
                await PersistUnlockedAsync(cancellationToken);
                LogScopeErased(_logger, scopeKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? HighestNonRevokedKeyId(string scopeKey)
    {
        if (!_state.Scopes.TryGetValue(scopeKey, out var entry))
        {
            return null;
        }

        string? best = null;
        var bestVersion = 0;
        foreach (var (keyId, wrapped) in entry.Keys)
        {
            if (wrapped.Revoked)
            {
                continue;
            }

            var version = ParseVersion(keyId);
            if (version > bestVersion)
            {
                bestVersion = version;
                best = keyId;
            }
        }

        return best;
    }

    private string CreateKeyUnlocked(string scopeKey, ReadOnlySpan<byte> kek)
    {
        if (!_state.Scopes.TryGetValue(scopeKey, out var entry))
        {
            entry = new ScopeEntry();
            _state.Scopes[scopeKey] = entry;
        }

        var version = entry.Keys.Count + 1;
        var keyId = $"{scopeKey}:v{version}";
        var dek = RandomNumberGenerator.GetBytes(CryptoConstants.KeySize);
        try
        {
            entry.Keys[keyId] = Wrap(dek, keyId, kek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        entry.CurrentKeyId = keyId;
        return keyId;
    }

    private static WrappedKeyEntry Wrap(byte[] dek, string keyId, ReadOnlySpan<byte> kek)
    {
        var nonce = RandomNumberGenerator.GetBytes(CryptoConstants.NonceSize);
        var tag = new byte[CryptoConstants.TagSize];
        var ciphertext = new byte[dek.Length];
        var aad = Encoding.UTF8.GetBytes(keyId);

        using (var gcm = new AesGcm(kek, CryptoConstants.TagSize))
        {
            gcm.Encrypt(nonce, dek, ciphertext, tag, aad);
        }

        return new WrappedKeyEntry
        {
            WrappedKeyBase64 = Convert.ToBase64String(ciphertext),
            WrapNonceBase64 = Convert.ToBase64String(nonce),
            WrapTagBase64 = Convert.ToBase64String(tag),
            CreatedAt = DateTimeOffset.UtcNow,
            Revoked = false,
        };
    }

    private static byte[] Unwrap(WrappedKeyEntry wrapped, string keyId, ReadOnlySpan<byte> kek)
    {
        var ciphertext = Convert.FromBase64String(wrapped.WrappedKeyBase64);
        var nonce = Convert.FromBase64String(wrapped.WrapNonceBase64);
        var tag = Convert.FromBase64String(wrapped.WrapTagBase64);
        var dek = new byte[ciphertext.Length];
        var aad = Encoding.UTF8.GetBytes(keyId);

        using var gcm = new AesGcm(kek, CryptoConstants.TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, dek, aad);
        return dek;
    }

    private static int ParseVersion(string keyId)
    {
        var marker = keyId.LastIndexOf(":v", StringComparison.Ordinal);
        return marker >= 0 && int.TryParse(keyId.AsSpan(marker + 2), out var version) ? version : 0;
    }

    private static string BuildScopeKey(KeyScope scope) => $"{scope.Level}:{scope.TenantId}:{scope.UserId}";

    private static KeyStoreFile LoadFromDisk(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new KeyStoreFile();
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<KeyStoreFile>(json, JsonOptions) ?? new KeyStoreFile();
    }

    private async Task PersistUnlockedAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        RestrictToOwner(tempPath);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    [LoggerMessage(EventId = LogEvents.KeyManagement.KeyCreated, Level = LogLevel.Information, Message = "Created data-encryption key {KeyId}.")]
    private static partial void LogKeyCreated(ILogger logger, string keyId);

    [LoggerMessage(EventId = LogEvents.KeyManagement.KeyRotated, Level = LogLevel.Information, Message = "Rotated to new data-encryption key {KeyId}.")]
    private static partial void LogKeyRotated(ILogger logger, string keyId);

    [LoggerMessage(EventId = LogEvents.KeyManagement.KeyRevoked, Level = LogLevel.Information, Message = "Revoked data-encryption key {KeyId}.")]
    private static partial void LogKeyRevoked(ILogger logger, string keyId);

    [LoggerMessage(EventId = LogEvents.KeyManagement.ScopeErased, Level = LogLevel.Information, Message = "Erased all key versions for scope {ScopeKey} (crypto-shred).")]
    private static partial void LogScopeErased(ILogger logger, string scopeKey);
}
