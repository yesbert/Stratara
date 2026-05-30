using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.Security;
using Stratara.Infrastructure.Security.KeyManagement;
using Stratara.Security;

namespace Stratara.Infrastructure.Tests.Security;

public class KeyStoreStartupProbeTests
{
    [Fact]
    public async Task StartAsync_ResolvesKeyStore_WithoutError()
    {
        var keyStore = new Mock<IKeyStore>().Object;
        var sut = new KeyStoreStartupProbe(NullLogger<KeyStoreStartupProbe>.Instance, keyStore);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var keyStore = new Mock<IKeyStore>().Object;
        var sut = new KeyStoreStartupProbe(NullLogger<KeyStoreStartupProbe>.Instance, keyStore);

        await sut.StartAsync(CancellationToken.None);
        var second = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(second);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutError()
    {
        var keyStore = new Mock<IKeyStore>().Object;
        var sut = new KeyStoreStartupProbe(NullLogger<KeyStoreStartupProbe>.Instance, keyStore);

        var exception = await Record.ExceptionAsync(async () => await sut.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_LogsWarning_WhenKeyStoreIsDummy()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        var dummy = new DummyKeyStore(env.Object);
        var logger = new RecordingLogger();
        var sut = new KeyStoreStartupProbe(logger, dummy);

        await sut.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("DummyKeyStore"));
    }

    [Fact]
    public async Task StartAsync_DoesNotWarn_WhenKeyStoreIsNotDummy()
    {
        var realKeyStore = new Mock<IKeyStore>().Object;
        var logger = new RecordingLogger();
        var sut = new KeyStoreStartupProbe(logger, realKeyStore);

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class RecordingLogger : ILogger<KeyStoreStartupProbe>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
