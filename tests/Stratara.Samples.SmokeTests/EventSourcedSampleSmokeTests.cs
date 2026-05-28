namespace Stratara.Samples.SmokeTests;

public sealed class EventSourcedSampleSmokeTests
{
    [Fact]
    public void EventSourced_RunsToCompletion_AndExposesEventStreamAndProjection()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.EventSourced");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Event-Sourced ===");
        result.ContainsInStdOut("Alice's balance: $135.00");
        result.ContainsInStdOut("Rejected:");
        result.ContainsInStdOut("AccountOpened");
        result.ContainsInStdOut("AmountDeposited");
        result.ContainsInStdOut("AmountWithdrawn");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
