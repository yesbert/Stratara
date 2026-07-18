namespace Stratara.Samples.SmokeTests;

public sealed class IdentityDirectorySampleSmokeTests
{
    [Fact]
    public void IdentityDirectory_RunsToCompletion_AndDemonstratesMembershipPermissionsAndSettings()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.IdentityDirectory");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Identity Directory ===");

        result.ContainsInStdOut("Alice in Acme: [TenantAdmin]");
        result.ContainsInStdOut("Alice in Globex: [Viewer]");
        result.ContainsInStdOut("Acme members: 2");
        result.ContainsInStdOut("Alice's active tenant: Globex");

        result.ContainsInStdOut("Alice @Acme — delete: allowed");
        result.ContainsInStdOut("Bob @Acme — delete: DENIED (missing sims.delete)");
        result.ContainsInStdOut("Alice @Globex — delete: DENIED (missing sims.delete)");

        result.ContainsInStdOut("Alice @Acme — Ui.Theme=dark, Ui.Density=compact");
        result.ContainsInStdOut("Bob @Acme — Ui.Theme=high-contrast, Ui.Density=comfortable");
        result.ContainsInStdOut("Alice @Globex — Ui.Theme=system, Ui.Density=comfortable");

        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
