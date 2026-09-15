namespace Stratara.Orleans.IntegrationTests.Hosting;

[Alias("Stratara.Orleans.IntegrationTests.IPingGrain")]
public interface IPingGrain : IGrainWithStringKey
{
    [Alias("PingAsync")]
    ValueTask<string> PingAsync();
}

public sealed class PingGrain : Grain, IPingGrain
{
    public ValueTask<string> PingAsync() => ValueTask.FromResult($"pong from {this.GetPrimaryKeyString()}");
}
