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
/// <remarks>
/// The store is safe for several processes that share one store file (for example containers
/// bind-mounting the same host directory). Mutations serialize through an exclusive cross-process
/// lock file and reload the latest on-disk state before mutating, so concurrent writers neither lose
/// each other's keys nor create colliding versions for the same scope. Reads that miss the in-memory
/// cache reload once from disk (guarded by the file's last-write time) to pick up keys another process
/// created after this instance started. A networked file system (NFS/SMB) is unsupported because it
/// guarantees neither atomic rename nor reliable advisory locks.
/// </remarks>
internal sealed partial class EnvelopeFileKeyStore : IKeyStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Poll interval while waiting for the cross-process lock file to become free.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Maximum number of lock-acquisition attempts (≈10s at <see cref="LockRetryDelay"/>).</summary>
    private const int MaxLockAttempts = 400;

    private readonly IMasterKeyProvider _masterKeyProvider;
    private readonly ILogger<EnvelopeFileKeyStore> _logger;
    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KeyStoreFile _state = new();
    private DateTime _lastLoadedWriteUtc;

    public EnvelopeFileKeyStore(
        IMasterKeyProvider masterKeyProvider,
        IOptions<StrataraFileKeyStoreOptions> options,
        ILogger<EnvelopeFileKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _masterKeyProvider = masterKeyProvider;
        _logger = logger;
        _filePath = Path.GetFullPath(options.Value.StorePath);
        _lockPath = _filePath + ".lock";
        ReloadStateUnlocked();
    }

    /// <inheritdoc/>
    public async ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        var scopeKey = BuildScopeKey(scope);
        var kek = await _masterKeyProvider.GetMasterKeyAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = await AcquireCrossProcessLockAsync(cancellationToken);

            // Re-evaluate on the freshest on-disk state so a scope another process created after our
            // start is reused instead of being recreated with a colliding ":v1" / divergent DEK.
            ReloadStateUnlocked();

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
            if (TryFindWrapped(keyId, out var wrapped))
            {
                return wrapped.Revoked ? null : Unwrap(wrapped, keyId, kek.Span);
            }

            // Cache miss: another process may have created this key since our last load. Reload once —
            // but only if the file actually changed, so repeated genuine misses (e.g. an erased scope)
            // don't trigger a reload storm. Writes commit via atomic rename, so an unlocked read is safe.
            if (DiskChangedSinceLastLoad())
            {
                ReloadStateUnlocked();
                if (TryFindWrapped(keyId, out wrapped))
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
            using var fileLock = await AcquireCrossProcessLockAsync(cancellationToken);
            ReloadStateUnlocked();

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
            using var fileLock = await AcquireCrossProcessLockAsync(cancellationToken);
            ReloadStateUnlocked();

            if (TryFindWrapped(keyId, out var wrapped) && !wrapped.Revoked)
            {
                wrapped.Revoked = true;
                await PersistUnlockedAsync(cancellationToken);
                LogKeyRevoked(_logger, keyId);
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
            using var fileLock = await AcquireCrossProcessLockAsync(cancellationToken);
            ReloadStateUnlocked();

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

    private bool TryFindWrapped(string keyId, out WrappedKeyEntry wrapped)
    {
        foreach (var scope in _state.Scopes.Values)
        {
            if (scope.Keys.TryGetValue(keyId, out var found))
            {
                wrapped = found;
                return true;
            }
        }

        wrapped = null!;
        return false;
    }

    private bool DiskChangedSinceLastLoad() =>
        File.Exists(_filePath) && File.GetLastWriteTimeUtc(_filePath) != _lastLoadedWriteUtc;

    private void ReloadStateUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            _state = new KeyStoreFile();
            _lastLoadedWriteUtc = DateTime.MinValue;
            return;
        }

        var previousWriteUtc = _lastLoadedWriteUtc;
        var json = File.ReadAllText(_filePath);
        _state = JsonSerializer.Deserialize<KeyStoreFile>(json, JsonOptions) ?? new KeyStoreFile();
        _lastLoadedWriteUtc = File.GetLastWriteTimeUtc(_filePath);

        // Only the initial load has no prior mtime; an unchanged reload is a no-op. Log solely when we
        // actually picked up newer on-disk content (i.e. another process wrote since our last load).
        if (previousWriteUtc != DateTime.MinValue && _lastLoadedWriteUtc != previousWriteUtc)
        {
            LogKeyStoreReloaded(_logger, _filePath);
        }
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoryExists();

        for (var attempt = 0; attempt < MaxLockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None maps to an exclusive advisory lock (flock) on Unix, shared across all
                // processes — and containers bind-mounting the same host inode — on the same kernel.
                // The OS releases it automatically if the holder crashes, so there is no stale-lock risk.
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }

        throw new TimeoutException($"Could not acquire the key-store lock '{_lockPath}' within the timeout.");
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async Task PersistUnlockedAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoryExists();

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        RestrictToOwner(tempPath);
        File.Move(tempPath, _filePath, overwrite: true);
        _lastLoadedWriteUtc = File.GetLastWriteTimeUtc(_filePath);
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

    [LoggerMessage(EventId = LogEvents.KeyManagement.KeyStoreReloaded, Level = LogLevel.Debug, Message = "Reloaded key-store state from {FilePath} to pick up keys written by another process.")]
    private static partial void LogKeyStoreReloaded(ILogger logger, string filePath);
}
