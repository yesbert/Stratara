using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Multitenancy;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public sealed class EfTenantMembershipStoreRoleTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConcurrentRoleWriter _concurrentWriter = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public EfTenantMembershipStoreRoleTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<TestDirectoryDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_concurrentWriter));
        services.AddTenantMembershipStore<TestDirectoryDbContext>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _scope.ServiceProvider.GetRequiredService<TestDirectoryDbContext>().Database.EnsureCreated();
        Store = _scope.ServiceProvider.GetRequiredService<ITenantMembershipStore>();
    }

    private ITenantMembershipStore Store { get; }

    [Fact]
    public async Task Adding_a_role_keeps_the_other_roles_and_the_status()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"], MembershipStatus.Pending));

        var changed = await Store.AddRolesAsync(_userId, _tenantId, ["TenantAdmin"]);

        Assert.Equal(["Editor", "TenantAdmin"], changed!.Roles);
        var stored = await Store.GetMembershipAsync(_userId, _tenantId);
        Assert.Equal(["Editor", "TenantAdmin"], stored!.Roles);
        Assert.Equal(MembershipStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Adding_a_role_already_held_holds_it_once()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));

        await Store.AddRolesAsync(_userId, _tenantId, ["Editor", "Viewer", "Viewer"]);

        Assert.Equal(["Editor", "Viewer"], (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles);
    }

    [Fact]
    public async Task Removing_a_role_keeps_the_others_and_ignores_one_not_held()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor", "Viewer", "TenantAdmin"]));

        await Store.RemoveRolesAsync(_userId, _tenantId, ["Viewer", "Auditor"]);

        Assert.Equal(["Editor", "TenantAdmin"], (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles);
    }

    [Fact]
    public async Task Changing_roles_without_a_membership_creates_none()
    {
        Assert.Null(await Store.AddRolesAsync(_userId, _tenantId, ["Editor"]));
        Assert.Null(await Store.RemoveRolesAsync(_userId, _tenantId, ["Editor"]));
        Assert.Null(await Store.GetMembershipAsync(_userId, _tenantId));
    }

    [Fact]
    public async Task A_role_change_that_meets_a_concurrent_writer_reads_again_and_keeps_both_changes()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));
        _concurrentWriter.WriteBeforeTheNextUpdates(1);

        var changed = await Store.AddRolesAsync(_userId, _tenantId, ["Viewer"]);

        Assert.Equal(["Editor", "Concurrent-1", "Viewer"], changed!.Roles);
        Assert.Equal(["Editor", "Concurrent-1", "Viewer"], (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles);
    }

    [Fact]
    public async Task A_role_change_that_loses_every_attempt_fails_instead_of_dropping_the_change()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));
        _concurrentWriter.WriteBeforeTheNextUpdates(int.MaxValue);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AddRolesAsync(_userId, _tenantId, ["Viewer"]));

        Assert.DoesNotContain("Viewer", (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles);
    }

    [Fact]
    public async Task A_whole_write_after_a_role_change_on_the_same_context_is_stored()
    {
        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));
        await Store.AddRolesAsync(_userId, _tenantId, ["Viewer"]);

        await Store.SetMembershipAsync(new TenantMembership(_userId, _tenantId, ["Editor"]));

        Assert.Equal(["Editor"], (await Store.GetMembershipAsync(_userId, _tenantId))!.Roles);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class ConcurrentRoleWriter : DbCommandInterceptor
    {
        private int _remaining;
        private int _written;

        public void WriteBeforeTheNextUpdates(int count) => _remaining = count;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_remaining > 0 && command.CommandText.Contains("UPDATE \"tenant_membership\"", StringComparison.Ordinal))
            {
                _remaining--;
                _written++;
                using var write = command.Connection!.CreateCommand();
                write.CommandText =
                    $"UPDATE \"tenant_membership\" SET \"Roles\" = '[\"Editor\",\"Concurrent-{_written}\"]'";
                write.ExecuteNonQuery();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
