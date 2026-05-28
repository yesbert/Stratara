using Stratara.Sessions.Multitenancy;

namespace Stratara.Shared.Tests.Multitenancy;

public class DefaultTenantIdentifierTests
{
    [Fact]
    public void Value_IsExpectedGuid()
    {
        var expected = Guid.Parse("DB5DB794-EDF0-4E50-9B50-D0105F694B52");

        Assert.Equal(expected, DefaultTenantIdentifier.Value);
    }
}
