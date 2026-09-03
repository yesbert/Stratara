using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Settings;
using Stratara.Identity.EntityFrameworkCore;
using Stratara.Sample.IdentityDirectory;

var alice = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
var bob = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
var acme = new Guid("11111111-1111-1111-1111-111111111111");
var globex = new Guid("22222222-2222-2222-2222-222222222222");

using var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddDbContext<DirectoryDbContext>(o => o.UseSqlite(connection));

builder.Services.AddSingleton<SampleSessionContextProvider>();
builder.Services.AddSingleton<ISessionContextProvider>(sp => sp.GetRequiredService<SampleSessionContextProvider>());

builder.Services
    .AddTenantMembershipStore<DirectoryDbContext>()
    .AddPermissionCatalog(c =>
    {
        c.Add(Permissions.SimulationsRead, Permissions.SimulationsDelete);
        c.GrantToRole("TenantAdmin", Permissions.SimulationsRead, Permissions.SimulationsDelete);
        c.GrantToRole("Viewer", Permissions.SimulationsRead);
    })
    .AddCatalogPermissionResolver()
    .AddSettingCatalog(c => c.Add(
        new SettingDefinition("Ui.Theme", DefaultValue: "system"),
        new SettingDefinition("Ui.Density", DefaultValue: "comfortable", IsInherited: false)))
    .AddSettingStore<DirectoryDbContext>();

builder.Services
    .AddAuthorizingMediator<MembershipAuthorizationProvider>()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

using var host = builder.Build();
var session = host.Services.GetRequiredService<SampleSessionContextProvider>();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DirectoryDbContext>().Database.EnsureCreatedAsync();
}

Console.WriteLine("=== Stratara Identity Directory ===");
Console.WriteLine();

Console.WriteLine("--- 1. Membership: one user, many tenants, roles scoped per tenant ---");
using (var scope = host.Services.CreateScope())
{
    var memberships = scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>();

    await memberships.SetMembershipAsync(new TenantMembership(alice, acme, ["TenantAdmin"]));
    await memberships.SetMembershipAsync(new TenantMembership(alice, globex, ["Viewer"]));
    await memberships.SetMembershipAsync(new TenantMembership(bob, acme, ["Viewer"]));

    foreach (var membership in await memberships.GetMembershipsAsync(alice))
    {
        Console.WriteLine($"  Alice in {Name(membership.TenantId)}: [{string.Join(", ", membership.Roles)}]");
    }

    Console.WriteLine($"  Acme members: {(await memberships.GetMembersAsync(acme)).Count}");

    await memberships.SetActiveTenantAsync(alice, globex);
    Console.WriteLine($"  Alice's active tenant: {Name(await memberships.GetActiveTenantAsync(alice) ?? Guid.Empty)}");
}
Console.WriteLine();

Console.WriteLine("--- 2. Permissions: membership roles mapped through the catalog ---");
await ActAs("Alice", alice, acme);
await ActAs("Bob", bob, acme);
await ActAs("Alice", alice, globex);
Console.WriteLine();

Console.WriteLine("--- 3. Settings: user-in-tenant -> user -> tenant -> global -> config -> default ---");
using (var scope = host.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<ISettingStore>();
    await store.SetAsync("Ui.Theme", "dark", SettingScope.ForTenant(acme));
    await store.SetAsync("Ui.Theme", "high-contrast", SettingScope.ForUserInTenant(acme, bob));
    await store.SetAsync("Ui.Density", "cozy", SettingScope.ForTenant(acme));
    await store.SetAsync("Ui.Density", "compact", SettingScope.ForUserInTenant(acme, alice));
    Console.WriteLine("  Stored Ui.Theme (inherited):     dark @Acme, high-contrast @Bob-in-Acme");
    Console.WriteLine("  Stored Ui.Density (NOT inherited): cozy @Acme, compact @Alice-in-Acme");
}

await ReadSettings("Alice", alice, acme);
await ReadSettings("Bob", bob, acme);
await ReadSettings("Alice", alice, globex);
Console.WriteLine();

Console.WriteLine("Done.");

async Task ActAs(string who, Guid userId, Guid tenantId)
{
    using var scope = host.Services.CreateScope();
    session.SignIn(userId, tenantId);
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

    var simulations = await mediator.HandleAsync(new ListSimulationsQuery());
    Console.WriteLine($"  {who} @{Name(tenantId)} — list: {simulations.Count} simulation(s)");

    try
    {
        await mediator.HandleAsync(new DeleteSimulationCommand("baseline-2026"));
        Console.WriteLine($"  {who} @{Name(tenantId)} — delete: allowed");
    }
    catch (PermissionAuthorizationException ex)
    {
        Console.WriteLine($"  {who} @{Name(tenantId)} — delete: DENIED (missing {ex.RequiredPermission})");
    }
}

async Task ReadSettings(string who, Guid userId, Guid tenantId)
{
    using var scope = host.Services.CreateScope();
    session.SignIn(userId, tenantId);
    var settings = scope.ServiceProvider.GetRequiredService<ISettingProvider>();

    var theme = await settings.GetOrNullAsync("Ui.Theme");
    var density = await settings.GetOrNullAsync("Ui.Density");
    Console.WriteLine($"  {who} @{Name(tenantId)} — Ui.Theme={theme}, Ui.Density={density}");
}

string Name(Guid id) =>
    id == acme ? "Acme" : id == globex ? "Globex" : id.ToString("D");
