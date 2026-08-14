using Stratara.Abstractions.ApiKeys;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class ApiKeyFormatTests
{
    [Fact]
    public void Created_keys_are_prefixed_well_formed_and_distinct()
    {
        var first = ApiKeyFormat.CreateRawKey();
        var second = ApiKeyFormat.CreateRawKey();

        Assert.StartsWith(ApiKeyFormat.Prefix, first, StringComparison.Ordinal);
        Assert.True(ApiKeyFormat.IsWellFormed(first));
        Assert.True(ApiKeyFormat.IsWellFormed(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Created_keys_carry_the_full_thirty_two_byte_payload()
    {
        var rawKey = ApiKeyFormat.CreateRawKey();

        // 32 bytes Base64Url-encoded without padding is 43 characters — the length the shape check
        // pins down, and the reason the stored digest can stay unsalted.
        Assert.Equal(ApiKeyFormat.Prefix.Length + 43, rawKey.Length);
    }

    [Fact]
    public void Malformed_values_are_rejected()
    {
        var wellFormed = ApiKeyFormat.CreateRawKey();

        Assert.False(ApiKeyFormat.IsWellFormed(null));
        Assert.False(ApiKeyFormat.IsWellFormed(""));
        Assert.False(ApiKeyFormat.IsWellFormed("   "));
        Assert.False(ApiKeyFormat.IsWellFormed("hunter2"));
        Assert.False(ApiKeyFormat.IsWellFormed(ApiKeyFormat.Prefix));
        Assert.False(ApiKeyFormat.IsWellFormed(wellFormed[..^1]));
        Assert.False(ApiKeyFormat.IsWellFormed(wellFormed + "a"));
        Assert.False(ApiKeyFormat.IsWellFormed("pat_" + wellFormed[4..]));
        Assert.False(ApiKeyFormat.IsWellFormed(wellFormed.ToUpperInvariant()[..4] + wellFormed[4..]));
    }

    [Fact]
    public void The_base64url_alphabet_is_enforced_character_by_character()
    {
        var wellFormed = ApiKeyFormat.CreateRawKey();

        foreach (var outsider in new[] { '!', '+', '/', '=', ' ', '.' })
        {
            Assert.False(ApiKeyFormat.IsWellFormed(wellFormed[..^1] + outsider));
        }

        Assert.True(ApiKeyFormat.IsWellFormed(wellFormed[..^1] + '-'));
        Assert.True(ApiKeyFormat.IsWellFormed(wellFormed[..^1] + '_'));
    }
}
