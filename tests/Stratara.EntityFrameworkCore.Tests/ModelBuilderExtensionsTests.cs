using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Constants;
using Stratara.EventSourcing.EntityFrameworkCore.Conventions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;
using Stratara.Abstractions.Entities;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

public class ModelBuilderExtensionsTests
{
    private static IMutableModel BuildModel(params Action<ModelBuilder>[] configure)
    {
        var builder = new ModelBuilder();
        var entity = builder.Entity<TestEntity>();
        entity.Property(e => e.Id);
        entity.Property(e => e.TenantId);
        entity.Property(e => e.Name);
        entity.Property(e => e.Description);
        entity.Property(e => e.DataJson);
        entity.Property(e => e.State);
        entity.Property<uint>("RowVersion");
        foreach (var action in configure)
        {
            action(builder);
        }

        return builder.Model;
    }

    [Fact]
    public void ApplyGlobalTenantQueryFilters_Adds_Filter_For_IMultiTenant()
    {
        var options = new DbContextOptionsBuilder<TestTenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TestTenantDbContext(options) { TenantId = Guid.NewGuid() };

        var model = BuildModel(mb => mb.ApplyGlobalTenantQueryFilters(dbContext));

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        Assert.NotEmpty(entityType.GetDeclaredQueryFilters());
    }

    [Fact]
    public void ApplyGlobalEntityConfiguration_Configures_Key_And_Id_Type()
    {
        var model = BuildModel(mb => mb.ApplyGlobalEntityConfiguration());

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        var key = entityType.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Contains(key.Properties, p => p.Name == nameof(IEntity.Id));

        var idProperty = entityType.FindProperty(nameof(IEntity.Id))!;
        Assert.Equal(ValueGenerated.OnAdd, idProperty.ValueGenerated);
        Assert.Equal("uuid", idProperty.GetColumnType());
    }

    [Fact]
    public void ApplyEnumStringConversion_Sets_String_Conversion_And_Max_Length()
    {
        var model = BuildModel(mb => mb.ApplyEnumStringConversion());

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        var prop = entityType.FindProperty(nameof(TestEntity.State))!;
        Assert.Equal(32, prop.GetMaxLength());
    }

    [Fact]
    public void ApplyDefaultStringLengths_Sets_Known_Lengths()
    {
        var model = BuildModel(mb => mb.ApplyDefaultStringLengths());

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        Assert.Equal(FieldLengthConstants.Name, entityType.FindProperty(nameof(TestEntity.Name))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Description, entityType.FindProperty(nameof(TestEntity.Description))!.GetMaxLength());
    }

    [Fact]
    public void ApplyDefaultStringLengths_Sets_All_Known_Property_Lengths()
    {
        var builder = new ModelBuilder();
        var entity = builder.Entity<ExtendedEntity>();
        entity.Property(e => e.Id);
        entity.Property(e => e.TenantId);
        entity.Property(e => e.Label);
        entity.Property(e => e.Code);
        entity.Property(e => e.Slug);
        entity.Property(e => e.Email);
        entity.Property(e => e.Phone);
        entity.Property(e => e.Url);
        builder.ApplyDefaultStringLengths();

        var entityType = builder.Model.FindEntityType(typeof(ExtendedEntity))!;
        Assert.Equal(FieldLengthConstants.Label, entityType.FindProperty(nameof(ExtendedEntity.Label))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Code, entityType.FindProperty(nameof(ExtendedEntity.Code))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Slug, entityType.FindProperty(nameof(ExtendedEntity.Slug))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Email, entityType.FindProperty(nameof(ExtendedEntity.Email))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Phone, entityType.FindProperty(nameof(ExtendedEntity.Phone))!.GetMaxLength());
        Assert.Equal(FieldLengthConstants.Url, entityType.FindProperty(nameof(ExtendedEntity.Url))!.GetMaxLength());
    }

    [Fact]
    public void ApplyGlobalTenantQueryFilters_Skips_NonTenant_Entities()
    {
        var options = new DbContextOptionsBuilder<TestTenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TestTenantDbContext(options) { TenantId = Guid.NewGuid() };

        var builder = new ModelBuilder();
        var nonTenantEntity = builder.Entity<NonTenantEntity>();
        nonTenantEntity.Property(e => e.Id);
        nonTenantEntity.Property(e => e.Name);
        builder.ApplyGlobalTenantQueryFilters(dbContext);

        var entityType = builder.Model.FindEntityType(typeof(NonTenantEntity))!;
        Assert.Empty(entityType.GetDeclaredQueryFilters());
    }

    [Fact]
    public void ApplyRowVersionConvention_InvalidMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildModel(mb => mb.ApplyRowVersionConvention((RowVersionMode)99)));
    }

    [Fact]
    public void ApplyJsonColumnConvention_InvalidProvider_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildModel(mb => mb.ApplyJsonColumnConvention((DatabaseProviderType)99)));
    }

    [Theory]
    [InlineData(DatabaseProviderType.PostgreSql, "jsonb")]
    [InlineData(DatabaseProviderType.SqlServer, "nvarchar(max)")]
    [InlineData(DatabaseProviderType.Sqlite, "text")]
    public void ApplyJsonColumnConvention_Sets_ColumnType(DatabaseProviderType provider, string expected)
    {
        var model = BuildModel(mb => mb.ApplyJsonColumnConvention(provider));

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        var prop = entityType.FindProperty(nameof(TestEntity.DataJson))!;
        var columnType = prop.FindAnnotation("Relational:ColumnType")?.Value as string;
        Assert.Equal(expected, columnType);
    }

    [Fact]
    public void ApplyRowVersionConvention_Uint_Sets_RowVersion_Concurrency()
    {
        var model = BuildModel(mb => mb.ApplyRowVersionConvention(RowVersionMode.Uint));

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        var prop = entityType.FindProperty("RowVersion");
        Assert.NotNull(prop);
        Assert.True(prop.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, prop.ValueGenerated);
    }

    [Fact]
    public void ApplyRowVersionConvention_ByteArray_Adds_Converter_And_Concurrency()
    {
        var model = BuildModel(mb => mb.ApplyRowVersionConvention(RowVersionMode.ByteArray));

        var entityType = model.FindEntityType(typeof(TestEntity))!;
        var prop = entityType.FindProperty("RowVersion");
        Assert.NotNull(prop);
        Assert.True(prop.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, prop.ValueGenerated);
        Assert.NotNull(prop.GetValueConverter());
    }

    private enum Status
    {
        A,
        B
    }

    private class TestEntity : IEntity, IMultiTenant, IHasRowVersion
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DataJson { get; set; } = string.Empty;
        public Status State { get; set; }
        public Guid Id { get; set; }
        public uint RowVersion { get; set; }
        public Guid TenantId { get; set; }
    }

    private class ExtendedEntity : IEntity, IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    private class NonTenantEntity : IEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestTenantDbContext(DbContextOptions<TestTenantDbContext> options)
        : DbContext(options), ITenantScopedDbContext
    {
        public Guid TenantId { get; init; }
    }
}
