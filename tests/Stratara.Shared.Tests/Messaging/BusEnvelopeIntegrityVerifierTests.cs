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
    public void Verify_NullSignature_StrictMode_RejectsWithoutConsultingSigner()
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", signature: null);

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedStrict, result);
        Assert.False(signer.VerifyWasCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_AbsentSignature_PermissiveMode_ReportsAbsent(string? signature)
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Permissive, "canonical", signature, out var failure);

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedPermissive, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.Absent, failure);
        Assert.False(signer.VerifyWasCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_AbsentSignature_StrictMode_ReportsAbsent(string? signature)
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", signature, out var failure);

        Assert.Equal(BusEnvelopeIntegrityResult.RejectedStrict, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.Absent, failure);
        Assert.False(signer.VerifyWasCalled);
    }

    [Theory]
    [InlineData(BusEnvelopeIntegrityMode.Permissive, BusEnvelopeIntegrityResult.RejectedPermissive)]
    [InlineData(BusEnvelopeIntegrityMode.Strict, BusEnvelopeIntegrityResult.RejectedStrict)]
    public void Verify_PresentSignatureRefused_ReportsInvalid(BusEnvelopeIntegrityMode mode, BusEnvelopeIntegrityResult expected)
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, mode, "canonical", "sig", out var failure);

        Assert.Equal(expected, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.Invalid, failure);
        Assert.True(signer.VerifyWasCalled);
    }

    [Fact]
    public void Verify_WhitespaceSignature_IsPresentAndInvalid_NotAbsent()
    {
        var signer = new RecordingSigner(verifyResult: false);

        BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Permissive, "canonical", "   ", out var failure);

        Assert.Equal(BusEnvelopeIntegrityFailure.Invalid, failure);
        Assert.True(signer.VerifyWasCalled);
    }

    [Fact]
    public void Verify_SignatureMatches_ReportsNoFailure()
    {
        var signer = new RecordingSigner(verifyResult: true);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Strict, "canonical", "sig", out var failure);

        Assert.Equal(BusEnvelopeIntegrityResult.Verified, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.None, failure);
    }

    [Fact]
    public void Verify_ModeOff_ReportsNoFailureEvenWhenUnsigned()
    {
        var signer = new RecordingSigner(verifyResult: false);

        var result = BusEnvelopeIntegrityVerifier.Verify(signer, BusEnvelopeIntegrityMode.Off, "canonical", signature: null, out var failure);

        Assert.Equal(BusEnvelopeIntegrityResult.Skipped, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.None, failure);
    }

    [Fact]
    public void Verify_NoSigner_ReportsNoFailureEvenWhenUnsigned()
    {
        var result = BusEnvelopeIntegrityVerifier.Verify(signer: null, BusEnvelopeIntegrityMode.Strict, "canonical", signature: null, out var failure);

        Assert.Equal(BusEnvelopeIntegrityResult.Skipped, result);
        Assert.Equal(BusEnvelopeIntegrityFailure.None, failure);
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
