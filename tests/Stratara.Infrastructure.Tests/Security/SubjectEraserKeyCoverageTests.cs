using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Infrastructure.Security;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Testing;
using Xunit;

namespace Stratara.Infrastructure.Tests.Security;

/// <summary>
/// <c>tenant-directory</c> → a subject's erasure removes its key material, so data encrypted under its keys becomes
/// unrecoverable. The serializer names a key by level, tenant and user together, so a subject's key material is every
/// scope that names it — at either isolating level, with or without the other dimension. Each test encrypts a value
/// the way an event's payload is encrypted, erases a subject, and reads the value back.
/// </summary>
public class SubjectEraserKeyCoverageTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    private sealed class DefaultLevelNote
    {
        [EncryptData]
        public string? Text { get; set; }
    }

    private sealed class TenantLevelNote
    {
        [EncryptData(DataSensitivityLevel.TenantScoped)]
        public string? Text { get; set; }
    }

    private sealed class Fixture
    {
        public InMemoryTenantMembershipStore Memberships { get; } = new();
        public InMemoryKeyStore Keys { get; } = new();

        public SecureJsonSerializer Serializer { get; }

        public Fixture()
        {
            var encryption = new ServiceCollection().AddStrataraBlobEncryption().BuildServiceProvider()
                .GetRequiredService<IEncryptionFactory>();
            Serializer = new SecureJsonSerializer(Keys, encryption, NullLogger<SecureJsonSerializer>.Instance, new ProductionEnvironment());
        }

        public SubjectEraser Eraser() =>
            new(Memberships, new InMemoryApiKeyStore(Memberships), new InMemorySettingStore(), Keys);

        public async Task<string> WriteAsync<T>(T note, Guid tenantId, Guid? userId) =>
            await Serializer.SerializeAsync(note, tenantId, userId);

        public async Task<string?> ReadAsync<T>(string json, Guid tenantId, Guid? userId) where T : class =>
            (await Serializer.DeserializeAsync<T>(json, tenantId, userId)) switch
            {
                DefaultLevelNote n => n.Text,
                TenantLevelNote n => n.Text,
                _ => null
            };
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task A_tenants_erasure_reaches_a_default_level_value_written_with_no_user()
    {
        var f = new Fixture();
        var json = await f.WriteAsync(new DefaultLevelNote { Text = "secret" }, Tenant, null);
        var elsewhere = await f.WriteAsync(new DefaultLevelNote { Text = "kept" }, OtherTenant, null);

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<DefaultLevelNote>(json, Tenant, null));
        Assert.Equal("kept", await f.ReadAsync<DefaultLevelNote>(elsewhere, OtherTenant, null));
    }

    [Fact]
    public async Task A_tenants_erasure_reaches_a_tenant_level_value_written_for_a_member()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);

        await f.Eraser().EraseTenantAsync(Tenant);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));
    }

    [Fact]
    public async Task A_users_erasure_reaches_a_tenant_level_value_written_for_that_user()
    {
        var f = new Fixture();
        await f.Memberships.SetMembershipAsync(new TenantMembership(Member, Tenant, ["member"]));
        var json = await f.WriteAsync(new TenantLevelNote { Text = "secret" }, Tenant, Member);
        var tenantWide = await f.WriteAsync(new TenantLevelNote { Text = "kept" }, Tenant, null);

        await f.Eraser().EraseUserAsync(Member);

        Assert.Null(await f.ReadAsync<TenantLevelNote>(json, Tenant, Member));
        Assert.Equal("kept", await f.ReadAsync<TenantLevelNote>(tenantWide, Tenant, null));
    }
}
