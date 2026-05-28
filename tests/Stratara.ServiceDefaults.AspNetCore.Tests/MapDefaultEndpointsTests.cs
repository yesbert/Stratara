using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class MapDefaultEndpointsTests
{
    [Fact]
    public async Task MapDefaultEndpoints_HealthEndpoint_Returns200ForUnauthenticatedCallerByDefault()
    {
        await using var app = await BuildAndStartAsync(requireAuthorizationOnHealth: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_AlivenessEndpoint_Returns200ForUnauthenticatedCaller()
    {
        await using var app = await BuildAndStartAsync(requireAuthorizationOnHealth: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/alive");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_AlivenessEndpoint_Returns200EvenWhenAuthorizationRequiredOnHealth()
    {
        await using var app = await BuildAndStartAsync(requireAuthorizationOnHealth: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/alive");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_HealthEndpoint_RejectsUnauthenticatedCallerWhenAuthorizationRequired()
    {
        await using var app = await BuildAndStartAsync(requireAuthorizationOnHealth: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/health");

        Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ReturnsSameAppForChaining()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddDefaultHealthChecks();
        await using var app = builder.Build();

        var returned = app.MapDefaultEndpoints();

        Assert.Same(app, returned);
    }

    private static async Task<WebApplication> BuildAndStartAsync(bool requireAuthorizationOnHealth)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddDefaultHealthChecks();
        if (requireAuthorizationOnHealth)
        {
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication("test").AddScheme<NoOpAuthOptions, NoOpAuthHandler>("test", _ => { });
        }

        var app = builder.Build();
        app.MapDefaultEndpoints(requireAuthorizationOnHealth);
        await app.StartAsync();
        return app;
    }
}

internal sealed class NoOpAuthOptions : Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions;

internal sealed class NoOpAuthHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<NoOpAuthOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : Microsoft.AspNetCore.Authentication.AuthenticationHandler<NoOpAuthOptions>(options, logger, encoder)
{
    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
}
