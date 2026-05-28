using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class EventStreamHashWorkerTests
{
    private readonly Mock<ILogger<EventStreamHashWorker>> _loggerMock = new();
    private readonly Mock<IEventStreamHashService> _hashServiceMock = new();
    private readonly Mock<IEventChainService> _chainServiceMock = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();

    public EventStreamHashWorkerTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IEventStreamHashService)))
            .Returns(_hashServiceMock.Object);
        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IEventChainService)))
            .Returns(_chainServiceMock.Object);

        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);

        _hashServiceMock
            .Setup(h => h.HashEventsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _chainServiceMock
            .Setup(c => c.AddAnchorIfNeededAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task StartAsync_LogsStartMessage()
    {
        var worker = new EventStreamHashWorker(_loggerMock.Object, _scopeFactoryMock.Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await worker.StartAsync(cts.Token);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopAsync_LogsStopMessage()
    {
        var worker = new EventStreamHashWorker(_loggerMock.Object, _scopeFactoryMock.Object);

        await worker.StopAsync(CancellationToken.None);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_CallsHashServiceAndChainService()
    {
        var worker = new EventStreamHashWorker(_loggerMock.Object, _scopeFactoryMock.Object);
        using var cts = new CancellationTokenSource();

        var callCount = 0;
        _hashServiceMock
            .Setup(h => h.HashEventsAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callCount++;
                if (callCount >= 1)
                {
                    cts.Cancel();
                }
            })
            .Returns(Task.CompletedTask);

        await worker.StartAsync(cts.Token);

        await Task.Delay(200);

        _hashServiceMock.Verify(h => h.HashEventsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _chainServiceMock.Verify(c => c.AddAnchorIfNeededAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionInHashService_ContinuesExecution()
    {
        var worker = new EventStreamHashWorker(_loggerMock.Object, _scopeFactoryMock.Object);
        using var cts = new CancellationTokenSource();

        var callCount = 0;
        _hashServiceMock
            .Setup(h => h.HashEventsAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("Test error");
                }

                if (callCount >= 2)
                {
                    cts.Cancel();
                }
            })
            .Returns(Task.CompletedTask);

        await worker.StartAsync(cts.Token);

        await Task.Delay(500);

        Assert.True(callCount >= 1);

        await worker.StopAsync(CancellationToken.None);
    }
}
