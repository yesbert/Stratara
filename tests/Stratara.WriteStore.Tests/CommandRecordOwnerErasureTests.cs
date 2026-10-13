using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Entities;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Repositories;
using Stratara.Infrastructure.DependencyInjection;
using Stratara.Infrastructure.Security;
using Stratara.Shared.Mediator.Mapping;
using Stratara.Testing;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

/// <summary>
/// <c>mediator-dispatch</c> → a recorded command remains after its owner is erased, and its protected
/// fields read as absent from then on. The record is written by the audit repository with the real
/// serializer and key store on a host outside development, and read back the way a reader of the
/// command log reads it: from the row's own data-owner columns.
/// </summary>
public class CommandRecordOwnerErasureTests
{
    private const string Address = "https://example.test/a-page-somebody-visited";

    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();
    private static readonly Guid Operator = Guid.CreateVersion7();

    public sealed record TrackPage(Guid Tenant, [property: EncryptData(DataSensitivityLevel.TenantScoped)] string? Address)
        : ICommand, INamesDataOwner
    {
        Guid INamesDataOwner.DataOwnerTenantId => Tenant;
    }

    [Fact]
    public async Task TheRecordOfACommandNamingAnOwner_FromASessionWithoutATenant_ReadsBackFromItsOwnColumns()
    {
        await using var fixture = new Fixture(SessionContext.Empty());

        var row = await fixture.RecordAsync(new TrackPage(Owner, Address));

        Assert.DoesNotContain(Address, row.CommandJson);
        Assert.Equal(Owner, row.TenantId);
        Assert.Equal(new TrackPage(Owner, Address), await fixture.ReadAsync(row));
    }

    [Fact]
    public async Task TheOwnersErasure_LeavesTheRecordAndEmptiesItsProtectedField()
    {
        await using var fixture = new Fixture(ASessionOfAnotherTenant());
        var row = await fixture.RecordAsync(new TrackPage(Owner, Address));

        await fixture.Eraser().EraseTenantAsync(Owner, TestContext.Current.CancellationToken);

        var remaining = await fixture.Context.Set<CommandLogEntry>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(row.Id, remaining.Id);
        Assert.Equal(Owner, remaining.TenantId);
        Assert.Equal(Operator, remaining.ActorUserId);
        Assert.False(string.IsNullOrWhiteSpace(remaining.CommandTypeName));
        Assert.Equal(new TrackPage(Owner, null), await fixture.ReadAsync(remaining));
    }

    [Fact]
    public async Task TheErasureOfTheDispatchingSessionsTenant_DoesNotReachTheRecordOfACommandNamingAnotherOwner()
    {
        await using var fixture = new Fixture(ASessionOfAnotherTenant());
        var row = await fixture.RecordAsync(new TrackPage(Owner, Address));

        await fixture.Eraser().EraseTenantAsync(OtherTenant, TestContext.Current.CancellationToken);

        Assert.Equal(new TrackPage(Owner, Address), await fixture.ReadAsync(row));
    }

    /// <summary>
    /// The copy carried to a handler and the recorded copy of a command that names an owner are written
    /// under one key, so one erasure reaches both.
    /// </summary>
    [Fact]
    public async Task BothCopiesOfACommandNamingAnOwner_AreWrittenUnderOneKey()
    {
        var session = ASessionOfAnotherTenant();
        await using var fixture = new Fixture(session);
        var command = new TrackPage(Owner, Address);

        var row = await fixture.RecordAsync(command);
        var envelope = await command.MapToAsync(session, fixture.Serializer, TestContext.Current.CancellationToken);

        Assert.Equal(KeyIdOf(row.CommandJson), KeyIdOf(envelope.CommandJson));
        Assert.Single(await fixture.Keys.ListScopesAsync(TestContext.Current.CancellationToken));
    }

    private static string? KeyIdOf(string commandJson)
    {
        using var document = JsonDocument.Parse(commandJson);
        return document.RootElement.GetProperty(nameof(TrackPage.Address)).GetProperty("kid").GetString();
    }

    private static SessionContext ASessionOfAnotherTenant() =>
        SessionContext.Empty() with { ActorTenantId = OtherTenant, ActorUserId = Operator, TenantId = OtherTenant };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InMemoryTenantMembershipStore _memberships = new();
        private readonly ServiceProvider _services;
        private readonly SessionContext _session;

        public InMemoryKeyStore Keys { get; } = new();
        public ISecureJsonSerializer Serializer { get; }
        public TestWriteDbContext Context { get; }

        public Fixture(SessionContext session)
        {
            _session = session;
            _services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IHostEnvironment>(new ProductionEnvironment())
                .AddSingleton<IKeyStore>(Keys)
                .AddSecurity()
                .BuildServiceProvider();
            Serializer = _services.GetRequiredService<ISecureJsonSerializer>();
            Context = new TestWriteDbContext(new DbContextOptionsBuilder<TestWriteDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        }

        public async Task<CommandLogEntry> RecordAsync(ICommandBase command)
        {
            var provider = new Mock<ISessionContextProvider>();
            provider.SetupGet(s => s.Current).Returns(_session);

            await new CommandAuditRepository(Context, provider.Object, Serializer).AddAsync(command, TestContext.Current.CancellationToken);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);

            return await Context.Set<CommandLogEntry>().SingleAsync(TestContext.Current.CancellationToken);
        }

        public async Task<TrackPage?> ReadAsync(CommandLogEntry row) =>
            await Serializer.DeserializeAsync<TrackPage>(row.CommandJson, row.TenantId, row.UserId, TestContext.Current.CancellationToken);

        public SubjectEraser Eraser() =>
            new(_memberships, new InMemoryApiKeyStore(_memberships), new InMemorySettingStore(), Keys);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
