using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Infrastructure.Security.Integrity;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="IBusEnvelopeSigner"/>. Keyed by
/// <see cref="BusEnvelopeIntegrityOptions.SharedKey"/>; the same key must be configured on
/// every publisher and consumer that share a bus.
/// </summary>
internal sealed class HmacBusEnvelopeSigner : IBusEnvelopeSigner
{
    private const int MinKeyLengthBytes = 32;

    private readonly byte[] _sharedKey;

    public HmacBusEnvelopeSigner(IOptions<BusEnvelopeIntegrityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = options.Value.SharedKey;
        if (key is null || key.Length < MinKeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"BusEnvelopeIntegrityOptions.SharedKey must be set to at least {MinKeyLengthBytes} bytes " +
                "before AddBusEnvelopeIntegrity can register HmacBusEnvelopeSigner.");
        }

        _sharedKey = (byte[])key.Clone();
    }

    public string Sign(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(_sharedKey, payloadBytes);
        return Convert.ToBase64String(signature);
    }

    public bool Verify(string payload, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expectedBase64 = Sign(payload);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedBase64);
        var actualBytes = Encoding.UTF8.GetBytes(signature);

        // Round-3-Audit Finding R3-Sec-008: CryptographicOperations.FixedTimeEquals throws
        // ArgumentException when the inputs differ in length. A short or long attacker signature
        // would otherwise surface as a noisy ArgumentException up the dispatch stack instead of
        // a clean false. Compare lengths first; a length-mismatch is by definition a non-match.
        if (expectedBytes.Length != actualBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
