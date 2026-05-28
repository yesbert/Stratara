using Stratara.Diagnostics;

namespace Stratara.Shared.Tests.Diagnostics;

public class ApplicationDiagnosticsTests
{
    [Fact]
    public void CorrelationIdTagName_HasExpectedValue()
    {
        Assert.Equal("correlation.id", ApplicationDiagnostics.CorrelationIdTagName);
    }

    [Fact]
    public void CausationIdTagName_HasExpectedValue()
    {
        Assert.Equal("causation.id", ApplicationDiagnostics.CausationIdTagName);
    }

    [Fact]
    public void TenantIdTagName_HasExpectedValue()
    {
        Assert.Equal("tenant.id", ApplicationDiagnostics.TenantIdTagName);
    }

    [Fact]
    public void UserIdTagName_HasExpectedValue()
    {
        Assert.Equal("user.id", ApplicationDiagnostics.UserIdTagName);
    }

    [Fact]
    public void ActivitySourceName_HasExpectedValue()
    {
        Assert.Equal("Stratara.Application", ApplicationDiagnostics.Activity.Source.Name);
    }
}
