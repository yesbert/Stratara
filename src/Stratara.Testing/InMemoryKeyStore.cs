using System.Collections.Concurrent;
using System.Security.Cryptography;
using Stratara.Abstractions.Security;

namespace Stratara.Testing;

/// <summary>
/// In-memory <see cref="IKeyStore"/> test double. Generates a random 256-bit data-encryption key
/// (DEK) per <see cref="KeyScope"/> on first use, hands the embedded key id back on lookup, and
/// supports rotation, revocation, and scope erasure with the same observable semantics as the
/// production <c>EnvelopeFileKeyStore</c> — but without KEK wrapping, on-disk files, or a
/// <see cref="IMasterKeyProvider"/>.
/// </summary>
/// <remarks>
/// Use it wherever production code depends on <see cref="IKeyStore"/> (blob encryption, field
/// encryption, crypto-shredding tests). Pair it with <see cref="TestBlobEncryptor.CreateAesGcm()"/>
/// to round-trip blobs through the real AES-GCM encryptor. Every returned key buffer is a fresh
/// copy, so callers that zero their copy after use (as the production encryptor does) do not
/// corrupt the stored material. All members are thread-safe.
/// </remarks>
public sealed class InMemoryKeyStore : IKeyStore
{
    private const int KeySizeInBytes = 32;

    private readonly ConcurrentDictionary<string, byte[]> _keysById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _currentByScope = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _counter;

    /// <inheritdoc />
    public ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var scopeKey = ScopeKey(scope);
            if (!_currentByScope.TryGetValue(scopeKey, out var keyId))
            {
                keyId = CreateKey(scopeKey);
            }

            return ValueTask.FromResult(new KeyMaterial(keyId, _keysById[keyId].ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        return ValueTask.FromResult(_keysById.TryGetValue(keyId, out var bytes) ? bytes.ToArray() : null);
    }

    /// <inheritdoc />
    public ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(CreateKey(ScopeKey(scope)));
        }
    }

    /// <inheritdoc />
    public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        _keysById.TryRemove(keyId, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var scopeKey = ScopeKey(scope);
            var prefix = scopeKey + "::";
            foreach (var keyId in _keysById.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _keysById.TryRemove(keyId, out _);
            }

            _currentByScope.TryRemove(scopeKey, out _);
        }

        return ValueTask.CompletedTask;
    }

    private string CreateKey(string scopeKey)
    {
        var keyId = $"{scopeKey}::v{Interlocked.Increment(ref _counter)}";
        _keysById[keyId] = RandomNumberGenerator.GetBytes(KeySizeInBytes);
        _currentByScope[scopeKey] = keyId;
        return keyId;
    }

    private static string ScopeKey(KeyScope scope) => $"{scope.Level}:{scope.TenantId}:{scope.UserId}";
}
