using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Infrastructure.Security.Integrity;

namespace Stratara.Infrastructure.Tests.Security;

public class HmacBusEnvelopeSignerTests
{
    private static readonly byte[] TestKey = new byte[32]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
    };

    private static IBusEnvelopeSigner CreateSigner(byte[]? key = null)
    {
        var options = Options.Create(new BusEnvelopeIntegrityOptions { SharedKey = key ?? TestKey });
        return new HmacBusEnvelopeSigner(options);
    }

    [Fact]
    public void Sign_ReturnsBase64EncodedSignature()
    {
        var signer = CreateSigner();

        var sig = signer.Sign("payload");

        Assert.False(string.IsNullOrEmpty(sig));
        Assert.Equal(44, sig.Length);
    }

    [Fact]
    public void Sign_IsDeterministicForSameKey()
    {
        var a = CreateSigner();
        var b = CreateSigner();

        Assert.Equal(a.Sign("hello"), b.Sign("hello"));
    }

    [Fact]
    public void Sign_DifferentPayloads_ProduceDifferentSignatures()
    {
        var signer = CreateSigner();

        Assert.NotEqual(signer.Sign("a"), signer.Sign("b"));
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var signer = CreateSigner();
        var sig = signer.Sign("payload");

        Assert.True(signer.Verify("payload", sig));
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var signer = CreateSigner();
        var sig = signer.Sign("payload");

        Assert.False(signer.Verify("paylo@d", sig));
    }

    [Fact]
    public void Verify_NullSignature_ReturnsFalse()
    {
        var signer = CreateSigner();

        Assert.False(signer.Verify("payload", null));
    }

    [Fact]
    public void Verify_EmptySignature_ReturnsFalse()
    {
        var signer = CreateSigner();

        Assert.False(signer.Verify("payload", string.Empty));
    }

    [Fact]
    public void Verify_DifferentKey_ReturnsFalse()
    {
        var alice = CreateSigner();
        var bob = CreateSigner(new byte[32] { 0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 });
        var aliceSig = alice.Sign("payload");

        Assert.False(bob.Verify("payload", aliceSig));
    }

    [Fact]
    public void Constructor_NullKey_Throws()
    {
        var options = Options.Create(new BusEnvelopeIntegrityOptions { SharedKey = null });

        Assert.Throws<InvalidOperationException>(() => new HmacBusEnvelopeSigner(options));
    }

    [Fact]
    public void Constructor_ShortKey_Throws()
    {
        var options = Options.Create(new BusEnvelopeIntegrityOptions { SharedKey = new byte[16] });

        Assert.Throws<InvalidOperationException>(() => new HmacBusEnvelopeSigner(options));
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("this-signature-is-much-longer-than-the-expected-44-chars-and-should-return-false-cleanly")]
    public void Verify_LengthMismatch_ReturnsFalse_WithoutThrowing(string mismatchSignature)
    {
        // Round-3-Audit Finding R3-Sec-008: CryptographicOperations.FixedTimeEquals throws
        // ArgumentException on length mismatch. The wrapper must catch the length difference
        // and return clean false instead of surfacing the exception.
        var signer = CreateSigner();

        var exception = Record.Exception(() => signer.Verify("payload", mismatchSignature));

        Assert.Null(exception);
        Assert.False(signer.Verify("payload", mismatchSignature));
    }
}
