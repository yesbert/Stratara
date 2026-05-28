namespace Stratara.Samples.SmokeTests;

public sealed class EncryptionSampleSmokeTests
{
    [Fact]
    public void Encryption_RunsToCompletion_SameTenantDecrypts_CrossTenantFails()
    {
        var result = SampleRunner.RunUntilExit("Stratara.Sample.Encryption");

        result.ExitCodeIs(0);
        result.ContainsInStdOut("=== Stratara Encryption ===");
        result.ContainsInStdOut("Seal the same SSN under two different tenants");
        result.ContainsInStdOut("tenant A reads tenant A");
        result.ContainsInStdOut("Cross-tenant attack");
        result.ContainsInStdOut("CAUGHT: AuthenticationTagMismatchException");
        result.ContainsInStdOut("AES-GCM rejected the authentication tag");
        result.StdOutEndsWith($"Done.{Environment.NewLine}");
    }
}
