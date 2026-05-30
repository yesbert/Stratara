namespace Stratara.Samples.SmokeTests;

public sealed class TamperProofSampleSmokeTests
{
    [Fact]
    public void TamperProof_RunsToCompletion_CleanVerifyPasses_TamperedVerifyIsCaught()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.TamperProof");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara TamperProof ===");
        result.ContainsInStdOut("Append three events");
        result.ContainsInStdOut("Verify the chain (clean)");
        result.ContainsInStdOut("OK — every entry's stored hash matches");

        // Act 1: anchor the clean chain to an external source of truth.
        result.ContainsInStdOut("Anchor the clean chain to an external source of truth");

        // Act 2: a naive tamper (stale hash) is caught by the local chain verifier.
        result.ContainsInStdOut("CAUGHT: Event stream tampering detected at sequence #2");

        // Act 3: a full re-chain fools the local verifier, but the external anchor catches it.
        result.ContainsInStdOut("PASSES — the local chain is internally consistent again");
        result.ContainsInStdOut("Verify against the external anchor");
        result.ContainsInStdOut("CAUGHT: Event stream tampering detected at sequence #3");

        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
