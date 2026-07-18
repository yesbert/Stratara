using Microsoft.EntityFrameworkCore;
using Stratara.Identity.EntityFrameworkCore.Configurations;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Adds the Stratara identity-directory tables (<c>tenant_membership</c>, <c>active_tenant</c>,
/// <c>setting_entry</c>, <c>api_key</c>) to an EF Core model. Call it from any DbContext's
/// <c>OnModelCreating</c> — typically the consumer's existing ASP.NET Identity context, so the
/// directory tables share that context's connection and migration lineage — or use
/// <see cref="IdentityDirectoryDbContext{TContext}"/> for a standalone context.
/// </summary>
public static class IdentityDirectoryModelBuilderExtensions
{
    /// <summary>
    /// Applies the identity-directory entity configurations to the model.
    /// </summary>
    /// <param name="builder">The model builder of the hosting DbContext.</param>
    /// <returns>The same builder for chaining.</returns>
    public static ModelBuilder ApplyIdentityDirectoryModel(this ModelBuilder builder)
    {
        builder.ApplyConfiguration(new TenantMembershipEntryConfiguration());
        builder.ApplyConfiguration(new ActiveTenantEntryConfiguration());
        builder.ApplyConfiguration(new SettingEntryConfiguration());
        builder.ApplyConfiguration(new ApiKeyEntryConfiguration());
        return builder;
    }
}
