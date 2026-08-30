using Stratara.Abstractions.Domain;
using Stratara.Abstractions.Reflections;
using Stratara.Infrastructure.EventSourcing;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// The public-setter constraint had no enforcement and no diagnostic: a violating aggregate rebuilds
/// successfully and loses the state its snapshot held, silently, and worse the better snapshotting
/// works.
/// </summary>
public class AggregateSnapshotShapeGuardTests
{
    private sealed class WellShapedAggregate : IAggregate
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Balance { get; set; }
    }

    private sealed class PrivateSetterAggregate : IAggregate
    {
        public Guid Id { get; set; }
        public string Name { get; private set; } = string.Empty;
        public decimal Balance { get; set; }
    }

    private sealed class ComputedPropertyAggregate : IAggregate
    {
        public Guid Id { get; set; }
        public decimal Balance { get; set; }
        public bool IsOverdrawn => Balance < 0;
    }

    private sealed class GetterOnlyAggregate : IAggregate
    {
        public Guid Id { get; set; }
        public decimal Balance { get; set; }
        public string Opened { get; } = "unset";
    }

    private sealed class NotAnAggregate
    {
        public string Name { get; private set; } = string.Empty;
    }

    private static AggregateSnapshotShapeGuard GuardOver(params Type[] types)
    {
        var resolver = new TrustedTypeResolver();
        foreach (var type in types)
        {
            resolver.Register(type);
        }

        return new AggregateSnapshotShapeGuard(resolver);
    }

    [Fact]
    public async Task AWellShapedAggregatePasses()
    {
        Assert.Null(await Record.ExceptionAsync(() =>
            GuardOver(typeof(WellShapedAggregate)).StartAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task APrivateSetterFailsStartUp_NamingTheAggregateAndTheProperty()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GuardOver(typeof(PrivateSetterAggregate)).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(PrivateSetterAggregate), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Name", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Balance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AComputedPropertyIsNotAViolation_BecauseItIsRecomputedAfterARestore()
    {
        Assert.Null(await Record.ExceptionAsync(() =>
            GuardOver(typeof(ComputedPropertyAggregate)).StartAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task AGetterOnlyAutoPropertyIsAViolation_BecauseItHoldsStateNothingCanRestore()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GuardOver(typeof(GetterOnlyAggregate)).StartAsync(CancellationToken.None));

        Assert.Contains("Opened", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypeThatIsNotAnAggregateIsIgnored()
    {
        Assert.Null(await Record.ExceptionAsync(() =>
            GuardOver(typeof(NotAnAggregate)).StartAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task EveryViolatingAggregateIsReported_NotJustTheFirst()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GuardOver(typeof(PrivateSetterAggregate), typeof(GetterOnlyAggregate), typeof(WellShapedAggregate))
                .StartAsync(CancellationToken.None));

        Assert.Contains(nameof(PrivateSetterAggregate), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(GetterOnlyAggregate), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(WellShapedAggregate), ex.Message, StringComparison.Ordinal);
    }
}
