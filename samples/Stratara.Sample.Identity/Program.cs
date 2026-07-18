using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.ApiKeys;
using Stratara.Identity.AspNetCore.Authentication;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Identity.EntityFrameworkCore;

var demoTenantId = new Guid("11111111-1111-1111-1111-111111111111");

using var connection = new SqliteConnection("DataSource=:memory:");
connection.Open();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SampleIdentityDbContext>(o => o.UseSqlite(connection));
builder.Services.AddIdentityCore<IdentityUser>(o => o.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<SampleIdentityDbContext>();

builder.Services
    .AddTenantMembershipStore<SampleIdentityDbContext>()
    .AddApiKeyStore<SampleIdentityDbContext>();

builder.Services
    .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    .AddCookie(IdentityConstants.ApplicationScheme)
    .AddStrataraOpenIdConnect(builder.Configuration)
    .AddStrataraJwtBearer(builder.Configuration)
    .AddStrataraApiKey()
    .AddStrataraAuthSchemeSelector(o => o.BearerScheme = JwtBearerDefaults.AuthenticationScheme);

builder.Services.AddStrataraExternalLoginProvisioning<IdentityUser>(o =>
    o.InvitationGate = (ctx, _) =>
        Task.FromResult(ctx.Email is not null && ctx.Email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)));

builder.Services.Configure<OpenIdConnectOptions>(
    OpenIdConnectDefaults.AuthenticationScheme,
    o => o.Events.OnTicketReceived = ProvisionOnFirstSignIn);

builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SampleIdentityDbContext>().Database.EnsureCreatedAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new
{
    message = "Stratara identity sample — external sign-in and API keys.",
    login = "/login  → challenges the configured OpenID Connect provider",
    api = "/api/me  → requires a valid Bearer access token",
    issueKey = "POST /admin/api-keys  → issues a machine key (raw value shown once)",
    useKey = "/api/whoami  → send the raw key as the X-Api-Key header",
}));

app.MapGet("/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/" },
    [OpenIdConnectDefaults.AuthenticationScheme]));

app.MapGet("/api/me", (ClaimsPrincipal user) =>
        Results.Ok(new { subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") }))
    .RequireAuthorization();

app.MapPost("/admin/api-keys", async (IApiKeyStore keys, CancellationToken cancellationToken) =>
{
    var issued = await keys.IssueAsync(
        new ApiKeyIssueRequest(demoTenantId, Name: "ci-pipeline", Roles: ["Viewer"]),
        cancellationToken);

    return Results.Ok(new
    {
        apiKey = issued.RawKey,
        note = "Shown exactly once — only a SHA-256 digest is stored. Send it as X-Api-Key.",
        keyId = issued.Descriptor.Id,
        tenant = issued.Descriptor.TenantId,
        roles = issued.Descriptor.Roles,
    });
});

app.MapGet("/api/whoami", (ClaimsPrincipal user) => Results.Ok(new
    {
        actor = user.FindFirstValue(ClaimTypes.NameIdentifier),
        tenant = user.FindFirstValue("stratara:tenant_id"),
        scheme = "resolved by the auth-scheme selector from the request shape",
    }))
    .RequireAuthorization();

app.Run();

static async Task ProvisionOnFirstSignIn(TicketReceivedContext context)
{
    var principal = context.Principal;
    if (principal is null)
    {
        context.Fail("No external principal.");
        return;
    }

    var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
    if (subject is null)
    {
        context.Fail("External login carried no subject identifier.");
        return;
    }

    var provisioning = context.HttpContext.RequestServices
        .GetRequiredService<ExternalLoginProvisioningService<IdentityUser>>();
    var scheme = context.Scheme.Name;
    var loginInfo = new ExternalLoginInfo(principal, scheme, subject, context.Scheme.DisplayName ?? scheme);

    var result = await provisioning.ProvisionAsync(loginInfo, context.HttpContext.RequestAborted);
    if (!result.Succeeded)
    {
        context.Response.Redirect($"/login-failed?reason={result.Outcome}");
        context.HandleResponse();
    }
}

internal sealed class SampleIdentityDbContext(DbContextOptions<SampleIdentityDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyIdentityDirectoryModel();
    }
}
