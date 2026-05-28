using Stratara.Abstractions.Messaging;

namespace Stratara.Shared.Tests.Messaging;

public class BusEnvelopeIntegrityVerifierTests
{
    [Fact]
    public void Verify_ModeOff_ReturnsSkipped()
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Off, "canonical", "sig");

        Assert.Equal(BusEnvelopeIntegrityResult.Skipped, result);
        Assert.False(signer.VerifyWasCalled);
    }

    [Fact]
    public void Verify_NoSigner_ReturnsSkipped()
    {
        var result = BusEnvelopeIntegrityVerifier.Verify(signer: null, BusEnvelopeIntegrityMode.Strict, "canonical", "sig");

        Assert.Equal(BusEnvelopeIntegrityResult.Skipped, result);
    }

    [Fact]
    public void Verify_SignatureMatches_ReturnsVerified()
    {
        var signer = new RecordingSigner(verifyResult: true);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", "sig");

        Assert.Equal(BusEnvelopeIntegrityResult.Verified, result);
        Assert.True(signer.VerifyWasCalled);
    }

    [Fact]
    public void Verify_SignatureMismatch_PermissiveMode_ReturnsRejectedPermissive()
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Permissive, "canonical", "sig");

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedPermissive, result);
    }

    [Fact]
    public void Verify_SignatureMismatch_StrictMode_ReturnsRejectedStrict()
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", "sig");

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedStrict, result);
    }

    [Fact]
    public void Verify_NullSignature_StrictMode_StillCallsSignerForLengthCheck()
    {
        // Verify behaviour: a missing signature is forwarded to IBusEnvelopeSigner.Verify which
        // per contract must return false for null/empty. The verifier then maps to RejectedStrict.
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", signature: null);

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedStrict, result);
        Assert.True(signer.VerifyWasCalled);
    }

    private sealed class RecordingSigner(bool verifyResult) : IBusEnvelopeSigner
    {
        public bool VerifyWasCalled { get; private set; }

        public string Sign(string payload) => "sig:" + payload;

        public bool Verify(string payload, string? signature)
        {
            VerifyWasCalled = true;
            return verifyResult;
        }
    }
}
