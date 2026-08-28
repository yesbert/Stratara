namespace Stratara.Documentation.Tests;

public class RegistrationSurfaceTests
{
    [Theory]
    [InlineData("AddRedisOutboxLock")]
    [InlineData("AddBusEnvelopeIntegrity")]
    [InlineData("AddProjectionReplayState")]
    [InlineData("AddCommandAuditing")]
    [InlineData("MapDefaultEndpoints")]
    public void Enumerate_FindsARegistration(string name) =>
        Assert.Contains(name, RegistrationSurface.Names());

    [Theory]
    [InlineData("AddRangeAsync")]
    [InlineData("MapTo")]
    [InlineData("AddPaymentCard")]
    [InlineData("AddAnchorAsync")]
    public void Enumerate_LeavesOutAMethodThatMerelyStartsWithAdd(string name) =>
        Assert.DoesNotContain(name, RegistrationSurface.Names());
}
