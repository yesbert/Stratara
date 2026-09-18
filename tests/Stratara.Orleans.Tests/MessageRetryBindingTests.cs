using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The execution model's command dispatcher adds <see cref="MessageRetryOptions"/>, so it reads the
/// <c>MessageRetry</c> section — once: where a bus transport registered before it already reads the section, the
/// dispatcher adds no second reading, and a value the host configured in code in between stays in effect.
/// </summary>
public sealed class MessageRetryBindingTests
{
    [Fact]
    public void The_dispatcher_without_a_bus_reads_the_section()
    {
        var builder = HostWithAttempts("7");

        builder.Services.AddStrataraOrleansCommandDispatcher();

        Assert.Equal(7, Resolve(builder.Services).MaxDeliveryAttempts);
    }

    [Fact]
    public void The_dispatcher_without_configuration_keeps_the_defaults()
    {
        var services = new ServiceCollection();

        services.AddStrataraOrleansCommandDispatcher();

        Assert.Equal(new MessageRetryOptions().MaxDeliveryAttempts, Resolve(services).MaxDeliveryAttempts);
    }

    [Fact]
    public void Code_after_the_dispatcher_wins()
    {
        var builder = HostWithAttempts("7");

        builder.Services.AddStrataraOrleansCommandDispatcher();
        builder.Services.Configure<MessageRetryOptions>(o => o.MaxDeliveryAttempts = 9);
        builder.Services.AddStrataraOrleansCommandDispatcher();

        Assert.Equal(9, Resolve(builder.Services).MaxDeliveryAttempts);
        Assert.Single(builder.Services, d => d.ImplementationType == typeof(MessageRetryOptionsBinding));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IValidateOptions<MessageRetryOptions>));
    }

    [Fact]
    public void A_bus_registered_before_is_the_only_reading_and_code_in_between_wins()
    {
        var builder = HostWithAttempts("7");

        builder.AddMessaging();
        builder.Services.Configure<MessageRetryOptions>(o => o.MaxDeliveryAttempts = 9);
        builder.Services.AddStrataraOrleansCommandDispatcher();

        Assert.Equal(9, Resolve(builder.Services).MaxDeliveryAttempts);
        Assert.DoesNotContain(builder.Services, d => d.ImplementationType == typeof(MessageRetryOptionsBinding));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IValidateOptions<MessageRetryOptions>));
    }

    [Fact]
    public void A_bus_registered_after_reads_the_section_last()
    {
        var builder = HostWithAttempts("7");

        builder.Services.AddStrataraOrleansCommandDispatcher();
        builder.AddMessaging();

        Assert.Equal(7, Resolve(builder.Services).MaxDeliveryAttempts);
    }

    [Fact]
    public async Task A_bound_of_zero_fails_the_start()
    {
        var builder = HostWithAttempts("0");
        builder.Services.AddStrataraOrleansCommandDispatcher();
        builder.Services.RemoveAll<IHostedService>();
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("MaxDeliveryAttempts", ex.Message, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder HostWithAttempts(string attempts)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MessageRetry:MaxDeliveryAttempts"] = attempts,
        });
        return builder;
    }

    private static MessageRetryOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<MessageRetryOptions>>().Value;
}
