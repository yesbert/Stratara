using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A framework exception thrown by a handler on one silo reaches a caller on another with its type, so a transport or a
/// worker there can tell a concurrency conflict, or a save that committed but could not publish, from any other failure.
/// The round trip is the one Orleans makes between silos.
/// </summary>
public sealed class FrameworkExceptionSerializationTests
{
    private static Exception? RoundTrip(Exception exception, bool registered)
    {
        var services = new ServiceCollection().AddSerializer();
        if (registered)
        {
            FrameworkExceptionSerialization.Register(services);
        }

        var serializer = services.BuildServiceProvider().GetRequiredService<Serializer>();
        return serializer.Deserialize<Exception>(serializer.SerializeToArray(exception));
    }

    [Fact]
    public void A_concurrency_conflict_keeps_its_type_and_reads_empty_where_its_properties_did_not_cross()
    {
        var back = Assert.IsType<ConcurrencyException>(RoundTrip(new ConcurrencyException(Guid.NewGuid(), "Order"), registered: true));

        Assert.Equal(string.Empty, back.AggregateTypeName);
        Assert.Contains("Order", back.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_that_committed_but_could_not_publish_keeps_its_type_and_inner_exception()
    {
        var streamId = Guid.NewGuid();
        var back = Assert.IsType<CommittedEventsNotPublishedException>(RoundTrip(
            new CommittedEventsNotPublishedException([streamId], 1, new InvalidOperationException("the bus is down")), registered: true));

        Assert.Empty(back.StreamIds);
        Assert.Contains(streamId.ToString(), back.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(back.InnerException);
    }

    [Fact]
    public void Without_the_registration_the_type_does_not_cross()
    {
        Assert.ThrowsAny<Exception>(() => RoundTrip(new ConcurrencyException(Guid.NewGuid(), "Order"), registered: false));
    }

    [Fact]
    public void The_execution_models_registrations_register_it()
    {
        var services = new ServiceCollection().AddStrataraOrleansCommandDispatcher();
        var options = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<ExceptionSerializationOptions>>();

        Assert.Contains(FrameworkExceptionSerialization.NamespacePrefix, options.Value.SupportedNamespacePrefixes);
    }
}
