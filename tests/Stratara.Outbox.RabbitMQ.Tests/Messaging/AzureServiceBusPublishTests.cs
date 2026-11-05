using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Messaging;
using Stratara.Outbox.AzureServiceBus.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.Messaging;

/// <summary>
/// Publishing on Azure Service Bus reuses one sender per topic: every sender holds an AMQP send link
/// until it is closed, so a sender per message would open a link per message that lives as long as
/// the client.
/// </summary>
public sealed class AzureServiceBusPublishTests
{
    private const string Topic = "command-topic";
    private const string OtherTopic = "event-bundle-topic";

    private readonly Mock<ServiceBusClient> _client = new();
    private readonly Dictionary<string, Mock<ServiceBusSender>> _senders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _created = new(StringComparer.Ordinal);

    public AzureServiceBusPublishTests()
    {
        _client
            .Setup(c => c.CreateSender(It.IsAny<string>()))
            .Returns((string topic) =>
            {
                _created[topic] = _created.GetValueOrDefault(topic) + 1;
                var sender = new Mock<ServiceBusSender>();
                sender
                    .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
                _senders[topic] = sender;
                return sender.Object;
            });
    }

    private AzureServiceBusBus CreateBus() =>
        new(NullLogger<AzureServiceBusBus>.Instance, _client.Object, Options.Create(new BusEnvelopeJsonOptions()), Options.Create(new MessageRetryOptions()));

    [Fact]
    public async Task TwoPublishesToOneTopic_UseOneSender()
    {
        var bus = CreateBus();

        await bus.PublishAsync(Topic, "first", TestContext.Current.CancellationToken);
        await bus.PublishAsync(Topic, "second", TestContext.Current.CancellationToken);

        Assert.Equal(1, _created[Topic]);
        _senders[Topic].Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EachTopic_GetsItsOwnSender()
    {
        var bus = CreateBus();

        await bus.PublishAsync(Topic, "command", TestContext.Current.CancellationToken);
        await bus.PublishAsync(OtherTopic, "bundle", TestContext.Current.CancellationToken);

        Assert.Equal(1, _created[Topic]);
        Assert.Equal(1, _created[OtherTopic]);
    }

    [Fact]
    public async Task DisposingTheBus_DisposesTheSendersItOpened()
    {
        var bus = CreateBus();
        await bus.PublishAsync(Topic, "command", TestContext.Current.CancellationToken);
        await bus.PublishAsync(OtherTopic, "bundle", TestContext.Current.CancellationToken);

        await bus.DisposeAsync();

        _senders[Topic].Verify(s => s.DisposeAsync(), Times.Once);
        _senders[OtherTopic].Verify(s => s.DisposeAsync(), Times.Once);
    }
}
