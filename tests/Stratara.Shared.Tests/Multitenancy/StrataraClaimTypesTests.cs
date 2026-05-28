using Stratara.Sessions.Multitenancy;

namespace Stratara.Shared.Tests.Multitenancy;

public class StrataraClaimTypesTests
{
    [Fact]
    public void TenantId_HasExpectedValue()
    {
        Assert.Equal("stratara:tenant_id", StrataraClaimTypes.TenantId);
    }
}
