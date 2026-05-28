namespace Stratara.Samples.SmokeTests;

internal static class SampleResultAsserts
{
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

    public static void StdOutEndsWith(this SampleResult result, string expected)
    {
        if (!result.StdOut.EndsWith(expected, StringComparison.Ordinal))
        {
            Assert.Fail(BuildDiagnostic(result, $"Expected sample stdout to end with '{expected.ReplaceLineEndings("\\n")}'"));
        }
    }

    private static string BuildDiagnostic(SampleResult result, string message) =>
        $"{message}{Environment.NewLine}" +
        $"--- Exit code: {result.ExitCode}{Environment.NewLine}" +
        $"--- StdErr ({result.StdErr.Length} chars):{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
        $"--- StdOut ({result.StdOut.Length} chars):{Environment.NewLine}{result.StdOut}";
}
