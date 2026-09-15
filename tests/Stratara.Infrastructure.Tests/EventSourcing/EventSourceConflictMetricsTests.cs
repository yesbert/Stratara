using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Diagnostics;
using Stratara.Infrastructure.Tests.Diagnostics;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// The conflict counter tags the aggregate with its simple type name — the value the appended-events counter
/// carries — although the stream stores the assembly-qualified name, so a dashboard joins the two series on one
/// value.
/// </summary>
public class EventSourceConflictMetricsTests
{
    private sealed class ConflictMetricsProbe
    {
        public int Value { get; set; }
    }

    private sealed record ConflictMetricsProbeTouched(int By);

    [Fact]
    public async Task A_conflict_is_tagged_with_the_simple_aggregate_type_name()
    {
        await using var host = EventStoreTestHost.Create(services => services
            .AddTrustedType<ConflictMetricsProbe>()
            .AddTrustedType<ConflictMetricsProbeTouched>());
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<ConflictMetricsProbe>(streamId, new ConflictMetricsProbeTouched(1));
            await events.SaveChangesAsync();
        });

        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstWriter = first.ServiceProvider.GetRequiredService<IEventSource>();
        var secondWriter = second.ServiceProvider.GetRequiredService<IEventSource>();
        await firstWriter.AppendAsync<ConflictMetricsProbe>(streamId, new ConflictMetricsProbeTouched(2));
        await secondWriter.AppendAsync<ConflictMetricsProbe>(streamId, new ConflictMetricsProbeTouched(3));
        await firstWriter.SaveChangesAsync();

        var (listener, measurements) = MeterCapture.Start();
        using (listener)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => secondWriter.SaveChangesAsync());
        }

        Assert.Contains(
            measurements.Snapshot(),
            m => m.Instrument == ApplicationDiagnostics.Metrics.EventSourceAppendConflictsName
                 && Equals(m.Tags.GetValueOrDefault(ApplicationDiagnostics.MetricTags.AggregateType), nameof(ConflictMetricsProbe)));
    }

    [Theory]
    [InlineData("OrderPlaced", "OrderPlaced")]
    [InlineData("Shop.Orders.OrderPlaced", "OrderPlaced")]
    [InlineData("Shop.Orders.OrderPlaced, Shop.Orders", "OrderPlaced")]
    [InlineData("Shop.Orders.OrderPlaced, Shop.Orders, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "OrderPlaced")]
    [InlineData("Shop.Orders.Order+Placed, Shop.Orders", "Placed")]
    [InlineData("Shop.Orders.Envelope`1[[Shop.Orders.OrderPlaced, Shop.Orders]], Shop.Orders", "Envelope`1")]
    public void A_type_tag_value_is_the_simple_type_name(string typeName, string expected) =>
        Assert.Equal(expected, ApplicationDiagnostics.MetricTags.TypeNameValue(typeName));
}
