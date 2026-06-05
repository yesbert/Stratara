using Xunit;

namespace Stratara.Testing.Tests;

public class AggregateTestHarnessTests
{
    [Fact]
    public void Given_then_build_rehydrates_state()
    {
        var id = Guid.CreateVersion7();

        var account = AggregateTestHarness<Account>
            .Given(new AccountOpened(id, "Ada", 100m))
            .And(new AmountDeposited(20m))
            .And(new AmountWithdrawn(30m))
            .Build();

        Assert.Equal(id, account.Id);
        Assert.Equal("Ada", account.Owner);
        Assert.Equal(90m, account.Balance);
    }

    [Fact]
    public void GivenNoEvents_returns_fresh_aggregate()
    {
        var account = AggregateTestHarness<Account>.GivenNoEvents().Build();

        Assert.Equal(Guid.Empty, account.Id);
        Assert.Equal(0m, account.Balance);
    }

    [Fact]
    public void Rehydrate_shortcut_matches_harness()
    {
        var id = Guid.CreateVersion7();

        var account = Aggregate.Rehydrate<Account>(
            new AccountOpened(id, "Ada", 100m),
            new AmountWithdrawn(40m));

        Assert.Equal(60m, account.Balance);
    }

    [Fact]
    public void Rehydrate_from_enumerable_applies_in_order()
    {
        var id = Guid.CreateVersion7();
        var events = new List<object> { new AccountOpened(id, "Ada", 0m), new AmountDeposited(5m), new AmountDeposited(5m) };

        var account = Aggregate.Rehydrate<Account>(events);

        Assert.Equal(10m, account.Balance);
    }

    [Fact]
    public void Unmapped_event_throws_by_default()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AggregateTestHarness<Account>.Given(new ProjectRenamed("nope")).Build());

        Assert.Contains("Apply(ProjectRenamed)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoringUnmappedEvents_skips_unknown_events()
    {
        var id = Guid.CreateVersion7();

        var account = AggregateTestHarness<Account>
            .Given(new AccountOpened(id, "Ada", 100m))
            .And(new ProjectRenamed("ignored"))
            .IgnoringUnmappedEvents()
            .Build();

        Assert.Equal(100m, account.Balance);
    }

    [Fact]
    public void Wrapped_apply_via_ievent_receives_payload_and_version()
    {
        var ledger = AggregateTestHarness<Ledger>
            .Given(new EntryPosted(10m))
            .And(new EntryPosted(15m))
            .Build();

        Assert.Equal(25m, ledger.Total);
        Assert.Equal(2L, ledger.LastVersion);
    }

    [Fact]
    public void Tenant_aggregate_rehydrates()
    {
        var project = Aggregate.Rehydrate<Project>(new ProjectRenamed("Apollo"));

        Assert.Equal("Apollo", project.Name);
    }
}
