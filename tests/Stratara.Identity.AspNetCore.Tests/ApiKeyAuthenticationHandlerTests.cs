using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Multitenancy;
using Stratara.Identity.AspNetCore.Authentication;
using Stratara.Sessions.Multitenancy;
using Stratara.Testing;
using Xunit;

namespace Stratara.Identity.AspNetCore.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid KeyId = Guid.CreateVersion7();
    private static readonly Guid BoundUserId = Guid.CreateVersion7();

    private static ApiKeyDescriptor MachineKey() =>
        new(KeyId, TenantId, null, "CI key", ["Deployer"], DateTimeOffset.UnixEpoch);

    private static ApiKeyDescriptor PersonalAccessToken() =>
        new(KeyId, TenantId, BoundUserId, "pat", [], DateTimeOffset.UnixEpoch);

    private static async Task<AuthenticateResult> AuthenticateAsync(
        IApiKeyStore store,
        Action<HttpContext>? configureRequest = null,
        Action<ApiKeyAuthenticationOptions>? configureOptions = null)
    {
        var options = new ApiKeyAuthenticationOptions();
        configureOptions?.Invoke(options);
        var monitor = Mock.Of<IOptionsMonitor<ApiKeyAuthenticationOptions>>(m =>
            m.Get(ApiKeyAuthenticationOptions.SchemeName) == options);

        var handler = new ApiKeyAuthenticationHandler(
            monitor, NullLoggerFactory.Instance, UrlEncoder.Default, store);

        var context = new DefaultHttpContext();
        configureRequest?.Invoke(context);

        await handler.InitializeAsync(
            new AuthenticationScheme(
                ApiKeyAuthenticationOptions.SchemeName,
                ApiKeyAuthenticationOptions.SchemeName,
                typeof(ApiKeyAuthenticationHandler)),
            context);

        return await handler.AuthenticateAsync();
    }

    private static Mock<IApiKeyStore> StoreReturning(string rawKey, ApiKeyDescriptor? descriptor)
    {
        var mock = new Mock<IApiKeyStore>();
        mock.Setup(s => s.ValidateAsync(rawKey, It.IsAny<CancellationToken>())).ReturnsAsync(descriptor);
        return mock;
    }

    [Fact]
    public async Task Valid_machine_key_authenticates_as_the_key_with_the_tenant_claim()
    {
        var result = await AuthenticateAsync(
            StoreReturning("stk_valid", MachineKey()).Object,
            ctx => ctx.Request.Headers["X-Api-Key"] = "stk_valid");

        Assert.True(result.Succeeded);
        var principal = result.Ticket!.Principal;
        Assert.Equal(KeyId.ToString("D"), principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(TenantId.ToString("D"), principal.FindFirstValue(StrataraClaimTypes.TenantId));
        Assert.Equal("CI key", principal.FindFirstValue(ClaimTypes.Name));
    }

    [Fact]
    public async Task Personal_access_token_authenticates_as_the_bound_user()
    {
        var result = await AuthenticateAsync(
            StoreReturning("stk_pat", PersonalAccessToken()).Object,
            ctx => ctx.Request.Headers["X-Api-Key"] = "stk_pat");

        Assert.True(result.Succeeded);
        Assert.Equal(
            BoundUserId.ToString("D"),
            result.Ticket!.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Missing_key_yields_no_result_so_other_schemes_may_run()
    {
        var result = await AuthenticateAsync(new Mock<IApiKeyStore>().Object);

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Invalid_key_fails_authentication()
    {
        var result = await AuthenticateAsync(
            StoreReturning("stk_bad", null).Object,
            ctx => ctx.Request.Headers["X-Api-Key"] = "stk_bad");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData(MembershipStatus.Former)]
    [InlineData(MembershipStatus.Pending)]
    public async Task Personal_access_token_without_an_active_membership_yields_no_principal(MembershipStatus status)
    {
        var memberships = new InMemoryTenantMembershipStore();
        var store = new InMemoryApiKeyStore(memberships);
        await memberships.SetMembershipAsync(new TenantMembership(BoundUserId, TenantId, []));
        var pat = await store.IssueAsync(new ApiKeyIssueRequest(TenantId, "pat", [], BoundUserId));

        var whileActive = await AuthenticateAsync(store, ctx => ctx.Request.Headers["X-Api-Key"] = pat.RawKey);
        Assert.True(whileActive.Succeeded);

        await memberships.SetMembershipAsync(new TenantMembership(BoundUserId, TenantId, [], status));

        var result = await AuthenticateAsync(store, ctx => ctx.Request.Headers["X-Api-Key"] = pat.RawKey);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task Query_string_keys_are_rejected_unless_opted_in()
    {
        var store = StoreReturning("stk_q", MachineKey());

        var withoutOptIn = await AuthenticateAsync(
            store.Object,
            ctx => ctx.Request.QueryString = new QueryString("?access_token=stk_q"));
        Assert.True(withoutOptIn.None);

        var withOptIn = await AuthenticateAsync(
            store.Object,
            ctx => ctx.Request.QueryString = new QueryString("?access_token=stk_q"),
            options => options.AllowQueryStringKey = true);
        Assert.True(withOptIn.Succeeded);
    }

    [Fact]
    public async Task Custom_header_name_is_honored()
    {
        var result = await AuthenticateAsync(
            StoreReturning("stk_custom", MachineKey()).Object,
            ctx => ctx.Request.Headers["X-My-Key"] = "stk_custom",
            options => options.HeaderName = "X-My-Key");

        Assert.True(result.Succeeded);
    }
}
