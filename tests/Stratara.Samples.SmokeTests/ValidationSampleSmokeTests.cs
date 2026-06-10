namespace Stratara.Samples.SmokeTests;

public sealed class ValidationSampleSmokeTests
{
    [Fact]
    public void Validation_RunsToCompletion_AndDemonstratesAllSeverities()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.Validation");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Validation ===");
        result.ContainsInStdOut("Accepted: ");
        result.ContainsInStdOut("Accepted despite the age warning");
        result.ContainsInStdOut("Rejected with 2 failure(s):");
        result.ContainsInStdOut("[email.invalid] Email:");
        result.ContainsInStdOut("[age.minimum] Age:");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
