using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class EventSourcingOptionsBindingTests
{
    [Fact]
    public async Task AnUndefinedNewStreamOwnerPolicy_IsRefusedAtStart()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EventSourcing:NewStreamOwnerFromSession"] = "3" });
        builder.Services.AddEventSourcing();
        using var host = builder.Build();

        var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("EventSourcing:NewStreamOwnerFromSession", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedPolicy_IsRead()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EventSourcing:NewStreamOwnerFromSession"] = "Warn" });
        builder.Services.AddEventSourcing();

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(NewStreamOwnerPolicy.Warn, provider.GetRequiredService<IOptions<EventSourcingOptions>>().Value.NewStreamOwnerFromSession);
    }
}
