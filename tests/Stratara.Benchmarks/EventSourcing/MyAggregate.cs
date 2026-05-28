namespace Stratara.Benchmarks;

public class MyAggregate
{
    public string State { get; private set; } = string.Empty;

    public void Apply(SomethingHappened e)
    {
        State = e.Value;
    }
}