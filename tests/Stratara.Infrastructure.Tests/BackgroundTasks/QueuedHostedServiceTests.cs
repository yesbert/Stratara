using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratara.Infrastructure.BackgroundTasks;
using Stratara.Abstractions.BackgroundTasks;

namespace Stratara.Infrastructure.Tests.BackgroundTasks;

public class QueuedHostedServiceTests
{
    private readonly Mock<ILogger<QueuedHostedService>> _loggerMock = new();
    private readonly Mock<IBackgroundTaskQueue> _taskQueueMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<IServiceScope> _scopeMock = new();

    public QueuedHostedServiceTests()
    {
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IServiceScopeFactory)))
            .Returns(Mock.Of<IServiceScopeFactory>());

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);

        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactoryMock.Object);
    }

    [Fact]
    public async Task StopAsync_LogsStopMessage()
    {
        var service = new QueuedHostedService(_loggerMock.Object, _serviceProviderMock.Object, _taskQueueMock.Object);

        await service.StopAsync(CancellationToken.None);

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
    public async Task ExecuteAsync_DequeuesAndExecutesTasks()
    {
        var taskExecuted = false;
        var taskId = Guid.NewGuid();
        Func<IServiceProvider, CancellationToken, ValueTask> workItem = (_, _) =>
        {
            taskExecuted = true;
            return ValueTask.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        var dequeueCount = 0;

        _taskQueueMock
            .Setup(q => q.DequeueAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                dequeueCount++;
                if (dequeueCount == 1)
                {
                    return new ValueTask<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>((taskId, workItem));
                }

                cts.Cancel();
                return new ValueTask<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>(
                    Task.FromCanceled<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>(cts.Token));
            });

        var service = new QueuedHostedService(_loggerMock.Object, _serviceProviderMock.Object, _taskQueueMock.Object);

        await service.StartAsync(cts.Token);

        await Task.Delay(300);

        Assert.True(taskExecuted);
        _taskQueueMock.Verify(q => q.UpdateTaskStatus(taskId, BackgroundTaskStatus.Running, null), Times.Once);
        _taskQueueMock.Verify(q => q.UpdateTaskStatus(taskId, BackgroundTaskStatus.Completed, null), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_TaskThrows_UpdatesStatusToFailed()
    {
        var taskId = Guid.NewGuid();
        Func<IServiceProvider, CancellationToken, ValueTask> failingTask = (_, _) =>
            throw new InvalidOperationException("Task failed");

        using var cts = new CancellationTokenSource();
        var dequeueCount = 0;

        _taskQueueMock
            .Setup(q => q.DequeueAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                dequeueCount++;
                if (dequeueCount == 1)
                {
                    return new ValueTask<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>((taskId, failingTask));
                }

                cts.Cancel();
                return new ValueTask<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>(
                    Task.FromCanceled<(Guid, Func<IServiceProvider, CancellationToken, ValueTask>)>(cts.Token));
            });

        var service = new QueuedHostedService(_loggerMock.Object, _serviceProviderMock.Object, _taskQueueMock.Object);

        await service.StartAsync(cts.Token);

        await Task.Delay(300);

        _taskQueueMock.Verify(q => q.UpdateTaskStatus(taskId, BackgroundTaskStatus.Failed, "Task failed"), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }
}
