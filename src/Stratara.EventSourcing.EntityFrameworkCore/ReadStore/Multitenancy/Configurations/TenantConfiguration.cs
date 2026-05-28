using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.Projections.Multitenancy.Models;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Multitenancy.Configurations;

/// <summary>
/// EF Core entity configuration for the <see cref="TenantView"/> read model. Relies on the
/// conventional defaults from
/// <see cref="Stratara.EventSourcing.EntityFrameworkCore.Extensions.ModelBuilderExtensions"/>; explicit
/// per-property mappings can be added here as the projection evolves.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TenantConfiguration : IEntityTypeConfiguration<TenantView>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<TenantView> builder)
    {
    }
}
