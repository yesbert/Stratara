using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.Http;

namespace Stratara.ServiceDefaults.Tests;

public class OpenTelemetryRedactionTests
{
    [Theory]
    [InlineData("http.request.header.authorization")]
    [InlineData("http.request.header.cookie")]
    [InlineData("http.request.header.proxy-authorization")]
    [InlineData("http.request.header.proxy_authorization")]
    [InlineData("http.response.header.set-cookie")]
    [InlineData("http.response.header.set_cookie")]
    public void OutgoingRequestEnrichment_RedactsCapturedCredentialHeaders(string tag)
    {
        var options = ResolveOptions();
        using var activity = new Activity("outgoing");
        activity.SetTag(tag, "secret");

        options.EnrichWithHttpRequestMessage!(activity, new HttpRequestMessage());

        Assert.Equal("REDACTED", activity.GetTagItem(tag));
    }

    [Fact]
    public void OutgoingResponseEnrichment_RedactsTheSetCookieHeader()
    {
        var options = ResolveOptions();
        using var activity = new Activity("outgoing");
        activity.SetTag("http.response.header.set-cookie", "session=secret");

        options.EnrichWithHttpResponseMessage!(activity, new HttpResponseMessage());

        Assert.Equal("REDACTED", activity.GetTagItem("http.response.header.set-cookie"));
    }

    [Fact]
    public void Enrichment_LeavesOtherHeadersAndAbsentTagsAlone()
    {
        var options = ResolveOptions();
        using var activity = new Activity("outgoing");
        activity.SetTag("http.request.header.content-type", "application/json");

        options.EnrichWithHttpRequestMessage!(activity, new HttpRequestMessage());

        Assert.Equal("application/json", activity.GetTagItem("http.request.header.content-type"));
        Assert.Null(activity.GetTagItem("http.request.header.authorization"));
    }

    private static HttpClientTraceInstrumentationOptions ResolveOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.ConfigureOpenTelemetry();

        return builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
    }
}
