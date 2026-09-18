using Microsoft.Extensions.DependencyInjection;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;
using Stratara.Contracts.Messages;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Scenarios <em>A host keeps the bus beside the grains with a dispatcher registered by a factory</em>, <em>A host
/// asks to keep the bus without a bus dispatcher</em> and <em>A second store-reading role asks to keep the bus</em>:
/// <c>hybrid: true</c> keeps the bus dispatcher whatever the shape of its registration, refuses a host that
/// registered none, and is answered on a later registration as on the first — never ignored.
/// </summary>
public sealed class HybridBundleDispatcherTests
{
    public static TheoryData<string> Shapes => ["type", "factory", "instance"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_bus_dispatcher_receives_the_bundle_whatever_the_shape_of_its_registration(string shape)
    {
        var services = Base();
        switch (shape)
        {
            case "type":
                services.AddSingleton<RecordingDispatcher>();
                services.AddScoped<IEventBundleOutboxDispatcher, RecordingDispatcherByType>();
                break;
            case "factory":
                services.AddSingleton<RecordingDispatcher>();
                services.AddScoped<IEventBundleOutboxDispatcher>(sp => sp.GetRequiredService<RecordingDispatcher>());
                break;
            default:
                services.AddSingleton<IEventBundleOutboxDispatcher>(Recorded);
                break;
        }

        services.AddStrataraProjectionGrains(hybrid: true);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>().EnqueueEventBundleAsync(Bundle(), TestContext.Current.CancellationToken);

        var recording = shape == "instance" ? Recorded : provider.GetRequiredService<RecordingDispatcher>();
        Assert.Equal(1, recording.Enqueued);
    }

    [Fact]
    public void Asking_for_the_bus_without_a_bus_dispatcher_fails_at_registration_naming_both()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Base().AddStrataraProjectionGrains(hybrid: true));

        Assert.Contains("hybrid", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddOutboxDispatcher", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_store_reading_role_that_asks_to_keep_the_bus_keeps_it()
    {
        var services = Base().AddSingleton<RecordingDispatcher>();
        services.AddScoped<IEventBundleOutboxDispatcher>(sp => sp.GetRequiredService<RecordingDispatcher>());

        services.AddStrataraProjectionGrains(hybrid: true);
        services.AddStrataraSagaGrains(hybrid: true);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>().EnqueueEventBundleAsync(Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.GetRequiredService<RecordingDispatcher>().Enqueued);
    }

    [Fact]
    public void A_second_store_reading_role_that_asks_to_keep_a_bus_nobody_registered_fails()
    {
        var services = Base();
        services.AddStrataraProjectionGrains();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddStrataraSagaGrains(hybrid: true));

        Assert.Contains("hybrid", failure.Message, StringComparison.Ordinal);
        Assert.Contains("AddOutboxDispatcher", failure.Message, StringComparison.Ordinal);
    }

    private static readonly RecordingDispatcher Recorded = new();

    private static IServiceCollection Base() => new ServiceCollection()
        .AddLogging()
        .AddSingleton(new Mock<IGrainFactory>().Object)
        .AddSingleton(new Mock<IProjectionReplayState>().Object)
        .AddSingleton(new Mock<IProjectionHandler>().Object);

    private static EventBundle Bundle() =>
        new([new EventMessage(Guid.NewGuid(), 1, "{}", Guid.NewGuid(), "Created", "Aggregate", Guid.Empty, Guid.Empty, Guid.Empty, null)], "{}");

    public class RecordingDispatcher : IEventBundleOutboxDispatcher
    {
        private int _enqueued;

        public int Enqueued => _enqueued;

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enqueued);
            return Task.CompletedTask;
        }

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class RecordingDispatcherByType(RecordingDispatcher recording) : IEventBundleOutboxDispatcher
    {
        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) =>
            recording.EnqueueEventBundleAsync(eventBundle, cancellationToken);

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
