using System.Security.Cryptography;
using System.Text;

namespace Stratara.Testing;

/// <summary>
/// Maps human-readable slugs to stable, deterministic <see cref="Guid"/> tenant ids, so test
/// assertions stay readable (<c>TestTenants.Of("acme")</c>) while the system still round-trips real
/// GUID identities. The same slug always yields the same id within and across test runs.
/// </summary>
/// <remarks>
/// The id is derived from a SHA-256 hash of the slug (first 16 bytes), not a random or RFC-4122
/// version-stamped value — its only contract is determinism and per-slug uniqueness. Use it for
/// tenant and user ids alike.
/// </remarks>
public static class TestTenants
{
    /// <summary>Return the deterministic id for <paramref name="slug"/>.</summary>
    /// <param name="slug">A stable, human-readable label (e.g. <c>"acme"</c>, <c>"contoso"</c>).</param>
    /// <returns>The same <see cref="Guid"/> every time for the same <paramref name="slug"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is null or whitespace.</exception>
    public static Guid Of(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        return new Guid(hash.AsSpan(0, 16));
    }
}
