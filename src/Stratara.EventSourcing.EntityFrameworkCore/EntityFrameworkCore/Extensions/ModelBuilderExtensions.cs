using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Constants;
using Stratara.EventSourcing.EntityFrameworkCore.Conventions;
using Stratara.EventSourcing.EntityFrameworkCore.ValueConverters;
using Stratara.EventSourcing.EntityFrameworkCore.ValueGenerators;
using Stratara.Abstractions.Entities;

namespace Stratara.EventSourcing.EntityFrameworkCore.Extensions;

/// <summary>
/// Conventional <see cref="ModelBuilder"/> helpers that apply Stratara's cross-cutting EF Core
/// model conventions: global tenant filters, GUIDv7 keys, row-version concurrency tokens,
/// enum-as-string storage, default string lengths, and provider-aware JSON columns.
/// </summary>
public static class ModelBuilderExtensions
{
    private const string RowVersion = "RowVersion";

    private static ModelBuilder ForEachEntityOfType<T>(ModelBuilder modelBuilder, Action<EntityTypeBuilder> configure)
    {
        foreach (var clrType in modelBuilder.Model.GetEntityTypes().Select(entityType => entityType.ClrType))
        {
            if (!typeof(T).IsAssignableFrom(clrType))
            {
                continue;
            }

            var builder = modelBuilder.Entity(clrType);
            configure(builder);
        }

        return modelBuilder;
    }

    private static int? GetStringLengthForProperty(string propertyName)
    {
        return propertyName switch
        {
            "Name" => FieldLengthConstants.Name,
            "Label" => FieldLengthConstants.Label,
            "Description" => FieldLengthConstants.Description,
            "Code" => FieldLengthConstants.Code,
            "Slug" => FieldLengthConstants.Slug,
            "Email" => FieldLengthConstants.Email,
            "Phone" => FieldLengthConstants.Phone,
            "Url" => FieldLengthConstants.Url,
            _ => null
        };
    }

    /// <summary>
    /// Extension members on <see cref="ModelBuilder"/> applied during <c>OnModelCreating</c>.
    /// </summary>
    extension(ModelBuilder modelBuilder)
    {
        /// <summary>
        /// Installs a global query filter on every entity that implements <c>IMultiTenant</c>,
        /// constraining its rows to <see cref="ITenantScopedDbContext.TenantId"/> of
        /// <paramref name="dbContext"/>.
        /// </summary>
        /// <typeparam name="TContext">The DbContext type that exposes the ambient tenant id.</typeparam>
        /// <param name="dbContext">The tenant-scoped DbContext supplying the active tenant.</param>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        public ModelBuilder ApplyGlobalTenantQueryFilters<TContext>(TContext dbContext) where TContext : DbContext, ITenantScopedDbContext
        {
            foreach (var clrType in modelBuilder.Model.GetEntityTypes().Select(entityType => entityType.ClrType))
            {
                if (!typeof(IMultiTenant).IsAssignableFrom(clrType))
                {
                    continue;
                }

                var parameter = Expression.Parameter(clrType, "e");

                var tenantIdOnEntity = Expression.Call(
                    typeof(EF),
                    nameof(EF.Property),
                    [typeof(Guid)],
                    parameter,
                    Expression.Constant(nameof(IMultiTenant.TenantId)));

                var currentTenantId = Expression.Property(
                    Expression.Constant(dbContext),
                    nameof(dbContext.TenantId));

                var body = Expression.Equal(tenantIdOnEntity, currentTenantId);
                var lambda = Expression.Lambda(body, parameter);

                modelBuilder.Entity(clrType).HasQueryFilter(lambda);
            }

            return modelBuilder;
        }

        /// <summary>
        /// Configures every <c>IEntity</c> in the model to use a <c>uuid</c> primary key
        /// generated on insert via <see cref="GuidV7ValueGenerator"/>.
        /// </summary>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        public ModelBuilder ApplyGlobalEntityConfiguration()
        {
            return ForEachEntityOfType<IEntity>(modelBuilder, builder =>
            {
                builder.HasKey(nameof(IEntity.Id));
                builder.Property(nameof(IEntity.Id))
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasValueGenerator<GuidV7ValueGenerator>();
            });
        }

        /// <summary>
        /// Adds a <c>RowVersion</c> shadow property to every entity that implements
        /// <c>IHasRowVersion</c>, optionally configured as a concurrency token, using the
        /// storage shape selected by <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">Whether the column is stored as a raw byte array or as <c>uint</c>.</param>
        /// <param name="useConcurrencyToken">When <c>true</c> the row version participates in optimistic concurrency.</param>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mode"/> is not a known <see cref="RowVersionMode"/>.</exception>
        public ModelBuilder ApplyRowVersionConvention(RowVersionMode mode,
            bool useConcurrencyToken = true)
        {
            foreach (var clrType in modelBuilder.Model.GetEntityTypes().Select(entityType => entityType.ClrType))
            {
                if (!typeof(IHasRowVersion).IsAssignableFrom(clrType))
                {
                    continue;
                }

                var builder = modelBuilder.Entity(clrType);
                switch (mode)
                {
                    case RowVersionMode.Uint:
                        var convertedRowVersion = builder
                            .Property<uint>(RowVersion)
                            .IsRowVersion();

                        if (useConcurrencyToken)
                        {
                            convertedRowVersion.IsConcurrencyToken();
                        }

                        break;
                    case RowVersionMode.ByteArray:
                        var rowVersion = builder
                            .Property<uint>(RowVersion)
                            .HasConversion(new ByteArrayToUIntConverter())
                            .IsRowVersion();
                        if (useConcurrencyToken)
                        {
                            rowVersion.IsConcurrencyToken();
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }
            }

            return modelBuilder;
        }

        /// <summary>
        /// Stores every enum property as a string column with a max length of 32 so persisted
        /// values are forward-compatible across enum re-ordering and renames stay explicit.
        /// </summary>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        public ModelBuilder ApplyEnumStringConversion()
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var properties = entityType.GetProperties()
                    .Where(p => p.ClrType.IsEnum);
                var builder = modelBuilder.Entity(entityType.ClrType);

                foreach (var property in properties)
                {
                    builder
                        .Property(property.Name)
                        .HasConversion<string>()
                        .HasMaxLength(32);
                }
            }

            return modelBuilder;
        }

        /// <summary>
        /// Applies the conventional max-length defaults from <see cref="FieldLengthConstants"/>
        /// to common string properties (<c>Name</c>, <c>Description</c>, <c>Email</c>, …) that
        /// have not been explicitly configured.
        /// </summary>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        public ModelBuilder ApplyDefaultStringLengths()
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var builder = modelBuilder.Entity(entityType.ClrType);
                var properties = entityType.GetProperties()
                    .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() == null);

                foreach (var propertyName in properties.Select(property => property.Name))
                {
                    var maxLength = GetStringLengthForProperty(propertyName);
                    if (maxLength.HasValue)
                    {
                        builder.Property(propertyName).HasMaxLength(maxLength.Value);
                    }
                }
            }

            return modelBuilder;
        }

        /// <summary>
        /// Maps any string property whose name contains <c>Json</c> to the
        /// provider-appropriate JSON column type (<c>jsonb</c> on PostgreSQL,
        /// <c>nvarchar(max)</c> on SQL Server, <c>text</c> on SQLite).
        /// </summary>
        /// <param name="providerType">The active relational provider.</param>
        /// <returns>The same <see cref="ModelBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="providerType"/> is not a known <see cref="DatabaseProviderType"/>.</exception>
        public ModelBuilder ApplyJsonColumnConvention(DatabaseProviderType providerType)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var builder = modelBuilder.Entity(entityType.ClrType);

                var jsonProperties = entityType
                    .GetProperties()
                    .Where(p => p.ClrType == typeof(string)
                                && p.Name.Contains("Json", StringComparison.OrdinalIgnoreCase));

                foreach (var property in jsonProperties)
                {
                    var propertyBuilder = builder.Property(property.Name);

                    switch (providerType)
                    {
                        case DatabaseProviderType.PostgreSql:
                            propertyBuilder.HasColumnType("jsonb");
                            break;

                        case DatabaseProviderType.SqlServer:
                            propertyBuilder.HasColumnType("nvarchar(max)");
                            break;

                        case DatabaseProviderType.Sqlite:
                            propertyBuilder.HasColumnType("text");
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(providerType), providerType, null);
                    }
                }
            }

            return modelBuilder;
        }
    }
}
