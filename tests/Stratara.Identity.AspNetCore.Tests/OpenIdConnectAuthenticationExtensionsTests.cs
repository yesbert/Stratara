using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}
