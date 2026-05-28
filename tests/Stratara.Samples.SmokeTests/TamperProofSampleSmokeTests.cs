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
        result.ContainsInStdOut("CAUGHT: Event stream tampering detected at sequence #2");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
