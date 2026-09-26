using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Stratara.Orleans.Tests;

/// <summary>The execution model's registrations have a cancelled grain call wait for the callee's answer.</summary>
public sealed class FrameworkCallCancellationTests
{
    [Fact]
    public void The_execution_models_registrations_wait_for_the_callees_answer()
    {
        using var provider = new ServiceCollection().AddStrataraOrleansCommandDispatcher().BuildServiceProvider();

        Assert.True(provider.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.WaitForCancellationAcknowledgement);
        Assert.True(provider.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.WaitForCancellationAcknowledgement);
    }
}
