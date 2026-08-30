namespace Stratara.Samples.SmokeTests;

/// <summary>
/// The smoke tests assert that a sample's output ends with "Done.", and six of the samples run a
/// generic host. The console logger writes on a background thread, so a lifetime entry queued
/// before the sample's last write can still land on stdout after it — the samples already stop the
/// host first, and it happens anyway. That made the outbox sample fail about one run in twelve.
///
/// These cases pin the stripping down deterministically, so the fix does not rest on a stress run
/// happening to come back clean.
/// </summary>
public class SampleResultAssertsTests
{
    private static SampleResult WithStdOut(string stdOut) => new(ExitCode: 0, StdOut: stdOut, StdErr: "");

    [Fact]
    public void StdOutEndsWith_PlainOutput_Passes() =>
        WithStdOut($"Balance: $135.00{Environment.NewLine}{Environment.NewLine}Done.{Environment.NewLine}")
            .StdOutEndsWith($"Done.{Environment.NewLine}");

    [Fact]
    public void StdOutEndsWith_HostLogEntryAfterTheFinalLine_Passes() =>
        WithStdOut(
            $"Done.{Environment.NewLine}"
            + $"info: Microsoft.Hosting.Lifetime[0]{Environment.NewLine}"
            + $"      Application is shutting down...{Environment.NewLine}")
            .StdOutEndsWith($"Done.{Environment.NewLine}");

    [Fact]
    public void StdOutEndsWith_HostLogEntriesInterleavedThroughout_Passes() =>
        WithStdOut(
            $"=== Sample ==={Environment.NewLine}"
            + $"info: Microsoft.Hosting.Lifetime[0]{Environment.NewLine}"
            + $"      Application started. Press Ctrl+C to shut down.{Environment.NewLine}"
            + $"  Balance: $135.00{Environment.NewLine}"
            + $"{Environment.NewLine}"
            + $"Done.{Environment.NewLine}"
            + $"info: Microsoft.Hosting.Lifetime[0]{Environment.NewLine}"
            + $"      Application is shutting down...{Environment.NewLine}")
            .StdOutEndsWith($"Done.{Environment.NewLine}");

    [Fact]
    public void StdOutEndsWith_SampleDidNotReachTheEnd_StillFails()
    {
        var failure = Record.Exception(() =>
            WithStdOut(
                $"  Balance: $135.00{Environment.NewLine}"
                + $"info: Microsoft.Hosting.Lifetime[0]{Environment.NewLine}"
                + $"      Application is shutting down...{Environment.NewLine}")
                .StdOutEndsWith($"Done.{Environment.NewLine}"));

        Assert.NotNull(failure);
    }

    [Fact]
    public void StdOutEndsWith_SampleOutputThatOnlyLooksLikeALogEntry_IsKept()
    {
        // "  info: ..." is indented sample output, not a console-logger header, which starts at
        // column zero. Stripping it would hide the sample's own text.
        var failure = Record.Exception(() =>
            WithStdOut($"  info: balance reconciled{Environment.NewLine}")
                .StdOutEndsWith($"  info: balance reconciled{Environment.NewLine}"));

        Assert.Null(failure);
    }
}
