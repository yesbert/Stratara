using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// The canonical raw-key format shared by every <see cref="IApiKeyStore"/> implementation:
/// <see cref="Prefix"/> followed by the Base64Url encoding of 32 CSPRNG bytes. The prefix helps
/// secret scanners flag leaked keys; the fixed length is what lets a store keep its stored digest
/// unsalted.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="CreateRawKey"/> whenever a key value has to exist <em>before</em> the store does
/// — container orchestration, CI provisioning, or a self-hosted bundle where caller and server read
/// the same key from configuration at start. Generate the value out of band, keep it in a secret
/// store, and hand it to <see cref="IApiKeyStore.ImportAsync"/> at boot. Never invent the value by
/// hand: <see cref="IApiKeyStore.ImportAsync"/> rejects anything that is not well-formed, because a
/// low-entropy key would turn the store's unsalted digest into a guessable one.
/// </para>
/// <para>
/// This type lives in the abstractions package on purpose — the projects that need to generate a
/// key (host builders, orchestration projects, test setups) usually do not reference the storage
/// implementation.
/// </para>
/// </remarks>
/// <example>
/// Generate a key once and keep it in configuration:
/// <code>
/// var rawKey = ApiKeyFormat.CreateRawKey();   // stk_…
/// </code>
/// </example>
public static class ApiKeyFormat
{
    /// <summary>
    /// The prefix every raw key carries (<c>stk_</c>) — a stable marker for secret scanners.
    /// </summary>
    public const string Prefix = "stk_";

    /// <summary>Number of random bytes behind a raw key (32 bytes = 256 bits of entropy).</summary>
    private const int RandomByteCount = 32;

    /// <summary>Length of the Base64Url encoding of <see cref="RandomByteCount"/> bytes, unpadded.</summary>
    private const int EncodedLength = 43;

    /// <summary>
    /// Creates a new raw key: <see cref="Prefix"/> plus the Base64Url encoding of
    /// <see cref="RandomByteCount"/> cryptographically random bytes.
    /// </summary>
    /// <returns>A well-formed raw key; treat it as a secret from the moment it exists.</returns>
    public static string CreateRawKey() =>
        Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomByteCount));

    /// <summary>
    /// Tests whether a value has the canonical shape — the prefix followed by exactly
    /// <see cref="EncodedLength"/> Base64Url characters.
    /// </summary>
    /// <remarks>
    /// A shape check cannot prove that the bytes behind the value are random; it can only rule out
    /// values that are structurally incapable of carrying 256 bits. That is the point: values that
    /// pass came from a generator, values a human typed do not.
    /// </remarks>
    /// <param name="rawKey">The candidate value.</param>
    /// <returns><c>true</c> when the value matches the canonical format.</returns>
    public static bool IsWellFormed([NotNullWhen(true)] string? rawKey)
    {
        if (rawKey is null || rawKey.Length != Prefix.Length + EncodedLength)
        {
            return false;
        }

        if (!rawKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in rawKey.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
