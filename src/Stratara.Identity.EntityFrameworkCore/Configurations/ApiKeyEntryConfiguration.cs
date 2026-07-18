using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stratara.Identity.EntityFrameworkCore.Configurations;

/// <summary>
/// EF Core mapping for the <c>api_key</c> table: unique index on the key hash (the validation
/// lookup), per-dimension indexes for the erasure sweeps, roles as a JSON string column.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class ApiKeyEntryConfiguration : IEntityTypeConfiguration<ApiKeyEntry>
{
    private const int HashedKeyMaxLength = 64;
    private const int NameMaxLength = 256;

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ApiKeyEntry> b)
    {
        b.ToTable("api_key");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.HashedKey).IsUnique();
        b.HasIndex(e => e.TenantId);
        b.HasIndex(e => e.UserId);

        b.Property(e => e.HashedKey).IsRequired().HasMaxLength(HashedKeyMaxLength);
        b.Property(e => e.Name).IsRequired().HasMaxLength(NameMaxLength);

        b.Property(e => e.Roles)
            .IsRequired()
            .HasConversion(
                roles => JsonSerializer.Serialize(roles, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonSerializerOptions.Default) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                    roles => roles.Aggregate(0, (hash, role) => HashCode.Combine(hash, role.GetHashCode())),
                    roles => roles.ToList()));
    }
}
