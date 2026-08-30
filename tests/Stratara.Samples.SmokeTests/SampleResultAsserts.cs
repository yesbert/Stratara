using System.Text.RegularExpressions;

namespace Stratara.Samples.SmokeTests;

internal static partial class SampleResultAsserts
{
    /// <summary>
    /// Opening line of a console-logger entry: <c>info: Microsoft.Hosting.Lifetime[0]</c>.
    /// </summary>
    [GeneratedRegex(@"^(trce|dbug|info|warn|fail|crit): ", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LogEntryHeader();

    public static void ContainsInStdOut(this SampleResult result, string expected)
    {
        if (!result.StdOut.Contains(expected, StringComparison.Ordinal))
        {
            Assert.Fail(BuildDiagnostic(result, $"Expected substring not found in sample stdout: '{expected}'"));
        }
    }

    public static void ExitCodeIs(this SampleResult result, int expected)
    {
        if (result.ExitCode != expected)
        {
            Assert.Fail(BuildDiagnostic(result, $"Expected sample exit code {expected}, got {result.ExitCode}"));
        }
    }

    /// <summary>
    /// Asserts that the sample's own output ends with <paramref name="expected"/>.
    ///
    /// Host logging is stripped first, because it is not written by the sample and does not
    /// obey its ordering. The console logger writes on a background thread, so a lifetime
    /// entry queued before the sample's last <c>Console.WriteLine</c> can still reach stdout
    /// after it — the samples already stop the host before printing, and it happens anyway.
    /// Asserting on raw stdout made this fail on roughly one run in twelve.
    /// </summary>
    public static void StdOutEndsWith(this SampleResult result, string expected)
    {
        var sampleOutput = WithoutHostLogging(result.StdOut);

        if (!sampleOutput.EndsWith(expected, StringComparison.Ordinal))
        {
            Assert.Fail(BuildDiagnostic(result, $"Expected sample stdout to end with '{expected.ReplaceLineEndings("\\n")}'"));
        }
    }

    /// <summary>
    /// Drops console-logger entries — a header line such as <c>info: Some.Category[0]</c> plus
    /// the indented message lines that belong to it — and leaves everything the sample printed,
    /// including its trailing newline.
    /// </summary>
    private static string WithoutHostLogging(string stdOut)
    {
        var lines = stdOut.ReplaceLineEndings("\n").Split('\n').ToList();

        // Split turns a trailing newline into a trailing empty element. Drop it and put the
        // newline back at the end, so "ends with Done.<newline>" still means what it says.
        var endedWithNewLine = lines.Count > 0 && lines[^1].Length == 0;
        if (endedWithNewLine)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var kept = new List<string>();
        var insideLogEntry = false;

        foreach (var line in lines)
        {
            if (LogEntryHeader().IsMatch(line))
            {
                insideLogEntry = true;
                continue;
            }

            // A log entry's message lines are indented. A blank line is the sample's own
            // spacing, never a continuation, so it ends the entry and is kept.
            if (insideLogEntry && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            insideLogEntry = false;
            kept.Add(line);
        }

        return string.Join(Environment.NewLine, kept) + (endedWithNewLine ? Environment.NewLine : string.Empty);
    }

    private static string BuildDiagnostic(SampleResult result, string message) =>
        $"{message}{Environment.NewLine}" +
        $"--- Exit code: {result.ExitCode}{Environment.NewLine}" +
        $"--- StdErr ({result.StdErr.Length} chars):{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
        $"--- StdOut ({result.StdOut.Length} chars):{Environment.NewLine}{result.StdOut}";
}
