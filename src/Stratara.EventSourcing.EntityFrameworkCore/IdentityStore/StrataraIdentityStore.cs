using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stratara.Identity.EntityFrameworkCore;

namespace Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

/// <summary>
/// The identity store several hosts can share on PostgreSQL: ASP.NET Identity's tables, passkeys
/// included, and the tenant directory — memberships, tenant selections, settings and API keys — with
/// the migrations that create and evolve them shipped in this package.
/// </summary>
/// <remarks>
/// <para>
/// The tables and the migration history live in the schema <see cref="Schema"/>, whose name is fixed
/// for every host and every release; the history table is <see cref="MigrationsHistoryTable"/>. Register
/// the store with <c>AddStrataraIdentityStore()</c>, apply its migrations with
/// <c>MigrateStrataraIdentityStoreAsync()</c>, and generate no migrations of your own for these tables.
/// </para>
/// <para>
/// The account type is <see cref="IdentityUser"/> and cannot be extended: every host sharing the store
/// sees the same columns. Keep what you know about a person beyond the account outside the identity
/// store. A host that does not share its store can still derive its own context from
/// <see cref="IdentityStore{TContext, TUser}"/> with a user type of its own.
/// </para>
/// <para>
/// Within a major version the schema changes only additively — new tables, new indexes, new columns
/// that are nullable or have a database default — so a host on an earlier release of the same major
/// version keeps working on a store a later release migrated.
/// </para>
/// </remarks>
/// <param name="options">Options bound by <c>AddStrataraIdentityStore()</c>.</param>
public sealed class StrataraIdentityStore(DbContextOptions<StrataraIdentityStore> options)
    : IdentityStore<StrataraIdentityStore, IdentityUser>(options)
{
    /// <summary>The schema the store's tables and its migration history live in.</summary>
    public const string Schema = "stratara_identity";

    /// <summary>The table, inside <see cref="Schema"/>, that records the store's applied migrations.</summary>
    public const string MigrationsHistoryTable = "__stratara_identity_migrations";

    /// <summary>
    /// The connection string the store reads from configuration; where it is absent, the host's default
    /// connection is used.
    /// </summary>
    public const string ConnectionStringName = "identitydb";

    private const int KeyMaxLength = 128;
    private const int PhoneNumberMaxLength = 256;

    /// <summary>
    /// The ASP.NET Identity schema version the store is built at — the one the framework's identity registration
    /// configures — whatever <see cref="StoreOptions.SchemaVersion"/> the host sets, so every host's model matches
    /// the shipped migrations. The key and phone-number lengths that ASP.NET Identity otherwise takes from the
    /// host's <see cref="StoreOptions"/> are fixed for the same reason.
    /// </summary>
    protected override Version SchemaVersion => IdentitySchemaVersions.Version3;

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<IdentityUser>(user => user.Property(u => u.PhoneNumber).HasMaxLength(PhoneNumberMaxLength));
        builder.Entity<IdentityUserLogin<string>>(login =>
        {
            login.Property(l => l.LoginProvider).HasMaxLength(KeyMaxLength);
            login.Property(l => l.ProviderKey).HasMaxLength(KeyMaxLength);
        });
        builder.Entity<IdentityUserToken<string>>(token =>
        {
            token.Property(t => t.LoginProvider).HasMaxLength(KeyMaxLength);
            token.Property(t => t.Name).HasMaxLength(KeyMaxLength);
        });
        builder.ApplyIdentityDirectoryModel();
    }
}
