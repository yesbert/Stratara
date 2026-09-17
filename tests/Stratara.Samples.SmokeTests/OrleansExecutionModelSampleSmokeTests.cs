namespace Stratara.Samples.SmokeTests;

public sealed class OrleansExecutionModelSampleSmokeTests
{
    [Fact]
    public void OrleansExecutionModel_RunsACommandAProjectionAndAProcessTimeout()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.OrleansExecutionModel", TimeSpan.FromSeconds(120));

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Orleans execution model ===");
        result.ContainsInStdOut("Command handled in the aggregate's activation (ActivationTaskScheduler).");
        result.ContainsInStdOut("Projection applied: Ada's balance is 100.00.");
        result.ContainsInStdOut("Process timeout fired: welcome sent to Ada.");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
