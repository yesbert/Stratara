using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The chain of aggregate turns: the innermost turn lets its own commands through, an outer turn refuses a send
/// back into it, a forwarded call carries the chain, and a recorded command starts a chain of its own.
/// </summary>
public sealed class AggregateTurnTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void Outside_any_turn_nothing_is_inside_or_enclosed()
    {
        Assert.Empty(AggregateTurn.Chain);
        Assert.False(AggregateTurn.IsInside(A));
        Assert.False(AggregateTurn.Encloses(A));
        Assert.Null(AggregateTurn.Carry());
    }

    [Fact]
    public void The_innermost_turn_is_inside_and_the_outer_ones_enclose()
    {
        using (AggregateTurn.Enter(A))
        using (AggregateTurn.Enter(B))
        {
            Assert.Equal([A, B], AggregateTurn.Chain);
            Assert.True(AggregateTurn.IsInside(B));
            Assert.False(AggregateTurn.IsInside(A));
            Assert.True(AggregateTurn.Encloses(A));
            Assert.False(AggregateTurn.Encloses(B));
            Assert.False(AggregateTurn.Encloses(C));
            Assert.Equal<Guid>([A, B], AggregateTurn.Carry() ?? []);
        }

        Assert.Empty(AggregateTurn.Chain);
    }

    [Fact]
    public void A_carried_chain_replaces_the_ambient_one_and_a_null_id_enters_nothing()
    {
        using (AggregateTurn.Enter(C))
        using (AggregateTurn.Enter(B, callerChain: [A]))
        {
            Assert.Equal([A, B], AggregateTurn.Chain);
            using (AggregateTurn.Enter(null))
            {
                Assert.Equal([A, B], AggregateTurn.Chain);
                Assert.True(AggregateTurn.IsInside(B));
            }
        }

        Assert.Empty(AggregateTurn.Chain);
    }

    [Fact]
    public void Leaving_a_turn_restores_the_chain_it_was_entered_from()
    {
        using (AggregateTurn.Enter(A))
        {
            using (AggregateTurn.Enter(B))
            {
                Assert.Equal([A, B], AggregateTurn.Chain);
            }

            Assert.Equal([A], AggregateTurn.Chain);
            Assert.True(AggregateTurn.IsInside(A));
        }
    }

    [Fact]
    public void The_refusal_names_the_sender_the_target_and_the_chain()
    {
        using (AggregateTurn.Enter(A))
        using (AggregateTurn.Enter(B))
        {
            var message = AggregateTurn.CycleMessage(A);
            Assert.StartsWith($"Aggregate {B} sent a command to aggregate {A}", message, StringComparison.Ordinal);
            Assert.Contains($"chain: {A} -> {B} -> {A}", message, StringComparison.Ordinal);
            Assert.Contains("must not form a cycle", message, StringComparison.Ordinal);
        }
    }
}
