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

        lock (_gate)
        {
            if (!_keysById.TryRemove(keyId, out _))
            {
                return ValueTask.CompletedTask;
            }

            var separator = keyId.LastIndexOf("::v", StringComparison.Ordinal);
            if (separator < 0)
            {
                return ValueTask.CompletedTask;
            }

            var scopeKey = keyId[..separator];
            if (!_currentByScope.TryGetValue(scopeKey, out var current) || current != keyId)
            {
                return ValueTask.CompletedTask;
            }

            var remaining = HighestRemainingKeyId(scopeKey);
            if (remaining is null)
            {
                _currentByScope.TryRemove(scopeKey, out _);
            }
            else
            {
                _currentByScope[scopeKey] = remaining;
            }
        }

        return ValueTask.CompletedTask;
    }

    private string? HighestRemainingKeyId(string scopeKey)
    {
        var prefix = scopeKey + "::v";

        return _keysById.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(k => int.Parse(k.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
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

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KeyScope>> ListScopesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<KeyScope> scopes = _keysById.Keys
                .Select(keyId => keyId[..keyId.LastIndexOf("::v", StringComparison.Ordinal)])
                .Concat(_currentByScope.Keys)
                .Distinct(StringComparer.Ordinal)
                .Select(ParseScopeKey)
                .OfType<KeyScope>()
                .ToList();
            return ValueTask.FromResult(scopes);
        }
    }

    private static string ScopeKey(KeyScope scope) => $"{scope.Level}:{scope.TenantId}:{scope.UserId}";

    private static KeyScope? ParseScopeKey(string scopeKey)
    {
        var first = scopeKey.IndexOf(':', StringComparison.Ordinal);
        var last = scopeKey.LastIndexOf(':');
        if (first < 0 || last == first || !Enum.TryParse<DataSensitivityLevel>(scopeKey[..first], out var level))
        {
            return null;
        }

        var tenantId = scopeKey[(first + 1)..last];
        var userId = scopeKey[(last + 1)..];
        return new KeyScope(level, tenantId.Length == 0 ? null : tenantId, userId.Length == 0 ? null : userId);
    }
}
