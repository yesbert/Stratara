using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class AspNetCoreOpenTelemetryRedactionTests
{
    [Theory]
    [InlineData("http.request.header.authorization")]
    [InlineData("http.request.header.cookie")]
    [InlineData("http.request.header.proxy-authorization")]
    [InlineData("http.request.header.proxy_authorization")]
    [InlineData("http.response.header.set-cookie")]
    [InlineData("http.response.header.set_cookie")]
    public void IncomingRequestEnrichment_RedactsCapturedCredentialHeaders(string tag)
    {
        var options = ResolveOptions();
        using var activity = new Activity("incoming");
        activity.SetTag(tag, "secret");

        options.EnrichWithHttpRequest!(activity, new DefaultHttpContext().Request);

        Assert.Equal("REDACTED", activity.GetTagItem(tag));
    }

    [Fact]
    public void IncomingResponseEnrichment_RedactsTheSetCookieHeader()
    {
        var options = ResolveOptions();
        using var activity = new Activity("incoming");
        activity.SetTag("http.response.header.set-cookie", "session=secret");

        options.EnrichWithHttpResponse!(activity, new DefaultHttpContext().Response);

        Assert.Equal("REDACTED", activity.GetTagItem("http.response.header.set-cookie"));
    }

    private static AspNetCoreTraceInstrumentationOptions ResolveOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.ConfigureAspNetOpenTelemetry();

        return builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
    }
}
