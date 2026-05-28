namespace Stratara.Samples.SmokeTests;

public sealed class OutboxWorkerSampleSmokeTests
{
    [Fact]
    public void OutboxWorker_RunsToCompletion_AndDrainsOutboxBeforeShutdown()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.OutboxWorker", TimeSpan.FromSeconds(90));

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Outbox + Worker ===");
        result.ContainsInStdOut("Enqueued — outbox has 3 pending");
        result.ContainsInStdOut("Outbox now has 0 pending");
        result.ContainsInStdOut("Balance: $175.00");
        result.ContainsInStdOut("Balance: $135.00");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
