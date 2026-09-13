namespace Stratara.Orleans.IntegrationTests.Hosting;

public interface IPingGrain : IGrainWithStringKey
{
    ValueTask<string> PingAsync();
}

public sealed class PingGrain : Grain, IPingGrain
{
    public ValueTask<string> PingAsync() => ValueTask.FromResult($"pong from {this.GetPrimaryKeyString()}");
}
