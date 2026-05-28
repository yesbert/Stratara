namespace Stratara.Samples.SmokeTests;

public sealed class CqrsBasicsSampleSmokeTests
{
    [Fact]
    public void CqrsBasics_RunsToCompletion_AndDemonstratesAllCqrsScenarios()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.CqrsBasics");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara CQRS Basics ===");
        result.ContainsInStdOut("Opened ");
        result.ContainsInStdOut("with $100");
        result.ContainsInStdOut("Balance after deposit: $150.00");
        result.ContainsInStdOut("Balance after withdraw: $75.00");
        result.ContainsInStdOut("Rejected:");
        result.ContainsInStdOut("cannot withdraw $999.00");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
