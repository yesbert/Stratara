using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Timers;
using Stratara.Contracts.Messages;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Singleton;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The published surface refuses what it cannot keep at the call and does no work it knows is empty:
/// a timer purpose the reminder store cannot hold is an argument error before anything is sent, and
/// the drain does not read stored bundles on a host whose bundle dispatcher never stores one.
/// </summary>
public sealed class SurfaceTrimTests
{
    [Fact]
    public async Task A_timer_purpose_longer_than_the_store_holds_is_refused_before_any_call()
    {
        var grains = new Mock<IGrainFactory>(MockBehavior.Strict);
        var timers = new DurableTimers(grains.Object);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            timers.RegisterAsync(new TimerRegistration("owner", new string('p', ReminderName.MaxPurposeLength + 1), DateTimeOffset.UtcNow)));

        Assert.Equal("purpose", refused.ParamName);
        grains.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("due@noon")]
    public async Task A_timer_purpose_that_cannot_be_encoded_is_refused(string purpose)
    {
        var timers = new DurableTimers(new Mock<IGrainFactory>(MockBehavior.Strict).Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            timers.RegisterAsync(new TimerRegistration("owner", purpose, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void A_purpose_of_the_longest_length_fits_the_reminder_name_column()
    {
        var name = ReminderName.Encode(new string('p', ReminderName.MaxPurposeLength), DateTimeOffset.MaxValue);

        Assert.True(name.Length <= 150, $"the reminder name has {name.Length} characters");
    }

    [Fact]
    public async Task The_drain_does_not_read_bundles_when_the_bundle_dispatcher_stores_none()
    {
        var repository = new Mock<IOutboxRepository>();
        repository
            .Setup(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.GetManyAsync<RecordedIntent>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var transaction = new Mock<ITransaction>();
        var unitOfWork = new Mock<IWriteUnitOfWork>();
        unitOfWork.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        unitOfWork.Setup(u => u.CreateOutboxRepository(transaction.Object)).Returns(repository.Object);

        var replay = new Mock<IProjectionReplayState>();
        var services = new ServiceCollection()
            .AddScoped(_ => unitOfWork.Object)
            .AddScoped(_ => new Mock<ICommandOutboxDispatcher>().Object)
            .AddScoped<IEventBundleOutboxDispatcher>(_ => new OrleansEventBundleDispatcher(
                new Mock<IGrainFactory>().Object,
                [],
                replay.Object,
                Options.Create(new CommitOrderOptions())))
            .BuildServiceProvider();

        var drain = new OutboxDrainWork(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new OutboxDrainOptions()));
        await drain.RunAsync(CancellationToken.None);

        repository.Verify(r => r.GetManyAsync<CommandEnvelope>(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.GetManyAsync<EventBundle>(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
