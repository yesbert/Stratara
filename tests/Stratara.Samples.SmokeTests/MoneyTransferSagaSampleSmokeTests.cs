namespace Stratara.Samples.SmokeTests;

public sealed class MoneyTransferSagaSampleSmokeTests
{
    [Fact]
    public void MoneyTransferSaga_RunsToCompletion_AndFansOutTransferIntoWithdrawDeposit()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.MoneyTransferSaga", TimeSpan.FromSeconds(90));

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Money-Transfer Saga ===");
        result.ContainsInStdOut("Alice: $200.00");
        result.ContainsInStdOut("Bob:   $50.00");
        result.ContainsInStdOut("Alice: $125.00");
        result.ContainsInStdOut("Bob:   $125.00");
        result.ContainsInStdOut("Rejected:");
        result.ContainsInStdOut("cannot withdraw $999.00");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
