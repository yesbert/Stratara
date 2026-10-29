using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

/// <summary>
/// The one place the shared identity store's options are built, so that the runtime registration and
/// the design-time factory the shipped migrations are generated with cannot diverge.
/// </summary>
internal static class StrataraIdentityStoreOptionsBuilder
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseSnakeCaseNamingConvention()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                StrataraIdentityStore.MigrationsHistoryTable, StrataraIdentityStore.Schema));
}

/// <summary>Builds the shared identity store for <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class StrataraIdentityStoreDesignTimeFactory : IDesignTimeDbContextFactory<StrataraIdentityStore>
{
    public StrataraIdentityStore CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StrataraIdentityStore>();
        StrataraIdentityStoreOptionsBuilder.Configure(options, "Host=localhost;Database=stratara_identity_design_time");
        return new StrataraIdentityStore(options.Options);
    }
}
