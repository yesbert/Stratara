using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.Identity.AspNetCore.Tests;

public class OpenIdConnectAuthenticationExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void BindOpenIdConnect_reads_the_section_and_keeps_secure_defaults()
    {
        var config = Config(
            ("Identity:OpenIdConnect:Authority", "https://login.example.com/v2.0"),
            ("Identity:OpenIdConnect:ClientId", "client-123"));

        var options = OpenIdConnectAuthenticationExtensions.BindOpenIdConnect(config, "Identity:OpenIdConnect");

        Assert.Equal("https://login.example.com/v2.0", options.Authority);
        Assert.Equal("client-123", options.ClientId);
        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.SaveTokens);
        Assert.Empty(options.Scopes);
    }

    [Fact]
    public void ApplyTo_oidc_applies_the_default_scopes_when_none_configured()
    {
        var source = OpenIdConnectAuthenticationExtensions.BindOpenIdConnect(
            Config(("Identity:OpenIdConnect:Authority", "https://issuer")), "Identity:OpenIdConnect");
        var target = new OpenIdConnectOptions();

        OpenIdConnectAuthenticationExtensions.ApplyTo(source, target);

        Assert.Equal(["openid", "profile", "email"], target.Scope);
    }

    [Fact]
    public void Configured_scopes_replace_the_defaults()
    {
        var config = Config(
            ("Identity:OpenIdConnect:Scopes:0", "openid"),
            ("Identity:OpenIdConnect:Scopes:1", "custom.scope"));

        var source = OpenIdConnectAuthenticationExtensions.BindOpenIdConnect(config, "Identity:OpenIdConnect");
        var target = new OpenIdConnectOptions();
        OpenIdConnectAuthenticationExtensions.ApplyTo(source, target);

        Assert.Equal(["openid", "custom.scope"], source.Scopes);
        Assert.Equal(["openid", "custom.scope"], target.Scope);
    }

    [Fact]
    public void ApplyTo_oidc_copies_scopes_and_keeps_sub_as_the_identifier()
    {
        var source = OpenIdConnectAuthenticationExtensions.BindOpenIdConnect(
            Config(("Identity:OpenIdConnect:Authority", "https://issuer")), "Identity:OpenIdConnect");
        var target = new OpenIdConnectOptions();

        OpenIdConnectAuthenticationExtensions.ApplyTo(source, target);

        Assert.Equal("https://issuer", target.Authority);
        Assert.Contains("openid", target.Scope);
        Assert.Contains("email", target.Scope);
        Assert.True(target.MapInboundClaims);
    }

    [Fact]
    public void BindJwtBearer_binds_multiple_issuers_for_a_multi_issuer_api()
    {
        var config = Config(
            ("Identity:JwtBearer:Audience", "api://stratara"),
            ("Identity:JwtBearer:ValidIssuers:0", "https://entra.example.com"),
            ("Identity:JwtBearer:ValidIssuers:1", "https://keycloak.example.com/realms/app"));

        var options = OpenIdConnectAuthenticationExtensions.BindJwtBearer(config, "Identity:JwtBearer");

        Assert.Equal("api://stratara", options.Audience);
        Assert.Equal(2, options.ValidIssuers.Count);
        Assert.Equal("sub", options.NameClaimType);
    }

    [Fact]
    public void ApplyTo_jwt_wires_issuer_and_audience_validation_and_disables_legacy_mapping()
    {
        var source = OpenIdConnectAuthenticationExtensions.BindJwtBearer(
            Config(
                ("Identity:JwtBearer:Audience", "api://stratara"),
                ("Identity:JwtBearer:ValidIssuers:0", "https://issuer-a"),
                ("Identity:JwtBearer:ValidIssuers:1", "https://issuer-b")),
            "Identity:JwtBearer");
        var target = new JwtBearerOptions();

        OpenIdConnectAuthenticationExtensions.ApplyTo(source, target);

        Assert.False(target.MapInboundClaims);
        Assert.Equal("sub", target.TokenValidationParameters.NameClaimType);
        Assert.True(target.TokenValidationParameters.ValidateIssuer);
        Assert.Contains("https://issuer-a", target.TokenValidationParameters.ValidIssuers!);
        Assert.True(target.TokenValidationParameters.ValidateAudience);
        Assert.Equal("api://stratara", target.TokenValidationParameters.ValidAudience);
    }

    [Fact]
    public async Task A_validated_bearer_principal_carries_its_subject_as_the_name_identifier()
    {
        var services = new ServiceCollection();
        services.AddAuthentication().AddStrataraJwtBearer(Config());

        var principal = await ValidateAsync(services, new Claim("sub", "6f1c0a52-3c39-4c47-9e0f-2f6d8e3c4b11"));

        Assert.Equal("6f1c0a52-3c39-4c47-9e0f-2f6d8e3c4b11", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("6f1c0a52-3c39-4c47-9e0f-2f6d8e3c4b11", principal.FindFirstValue("sub"));
    }

    [Fact]
    public async Task A_name_identifier_the_token_already_carries_is_kept()
    {
        var services = new ServiceCollection();
        services.AddAuthentication().AddStrataraJwtBearer(Config());

        var principal = await ValidateAsync(services, new Claim("sub", "subject"), new Claim(ClaimTypes.NameIdentifier, "explicit"));

        Assert.Equal("explicit", Assert.Single(principal.FindAll(ClaimTypes.NameIdentifier)).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_token_validated_handler_the_host_configures_still_runs(bool hostConfiguresFirst)
    {
        var services = new ServiceCollection();
        string? seenByHost = null;
        void HostHandler(JwtBearerOptions options) => options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                seenByHost = context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier);
                return Task.CompletedTask;
            },
        };

        if (hostConfiguresFirst)
        {
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, HostHandler);
        }

        services.AddAuthentication().AddStrataraJwtBearer(Config());
        if (!hostConfiguresFirst)
        {
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, HostHandler);
        }

        var principal = await ValidateAsync(services, new Claim("sub", "subject"));

        Assert.Equal("subject", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("subject", seenByHost);
    }

    private static async Task<ClaimsPrincipal> ValidateAsync(IServiceCollection services, params Claim[] claims)
    {
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext(new DefaultHttpContext { RequestServices = provider }, scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", "sub", "roles")),
        };

        await options.Events.TokenValidated(context);

        return context.Principal!;
    }
}
