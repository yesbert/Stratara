using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

/// <summary>
/// Generic ASP.NET Core Identity DbContext that hosts the <c>AspNetUsers</c>, <c>AspNetRoles</c>,
/// and related identity tables. Adds an <c>AspNetUserPasskeys</c> table for WebAuthn / passkey
/// credentials with JSON-stored credential data.
/// </summary>
/// <typeparam name="TContext">The concrete derived DbContext type (used for <see cref="DbContextOptions{TContext}"/> binding).</typeparam>
/// <typeparam name="TUser">The identity user entity, deriving from <see cref="IdentityUser"/>.</typeparam>
/// <remarks>
/// Filters <c>ApplyConfigurationsFromAssembly</c> by namespace so the write-store and read-store
/// <c>IEntityTypeConfiguration&lt;&gt;</c> implementations co-hosted in the same assembly do not
/// leak into the identity model. Missing the filter would surface as
/// <c>PendingModelChangesWarning</c> at runtime against a real Postgres instance.
/// </remarks>
/// <param name="options">Options bound by the host's <c>AddNpgsqlIdentityDbContextFactory</c> registration.</param>
public class IdentityStore<TContext, TUser>(DbContextOptions<TContext> options)
    : IdentityDbContext<TUser>(options), IIdentityDbContext where TContext : DbContext where TUser : IdentityUser
{
    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(
            Assembly.GetAssembly(typeof(IIdentityStoreMarker)) ?? throw new InvalidOperationException(),
            t => t.Namespace?.StartsWith("Stratara.EventSourcing.EntityFrameworkCore.IdentityStore", StringComparison.Ordinal) == true);

        builder.Entity<IdentityUserPasskey<string>>(b =>
        {
            b.HasKey(p => p.CredentialId);
            b.ToTable("AspNetUserPasskeys");
            b.HasOne<TUser>().WithMany().HasForeignKey(p => p.UserId).IsRequired();

            b.OwnsOne(p => p.Data, data =>
            {
                data.ToJson();
            });
        });
    }
}
