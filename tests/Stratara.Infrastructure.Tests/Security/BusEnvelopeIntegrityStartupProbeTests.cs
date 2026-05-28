using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Infrastructure.Security.Integrity;

namespace Stratara.Infrastructure.Tests.Security;

public class BusEnvelopeIntegrityStartupProbeTests
{
    [Fact]
    public async Task StartAsync_OffMode_InProduction_LogsWarning()
    {
        // Round-3-Audit Finding R3-Sec-009: operator running production without integrity must
        // see the security-relevant deviation in their log pipeline at host start.
        var env = ProductionEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options);

        await sut.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Off"));
    }

    [Fact]
    public async Task StartAsync_OffMode_InDevelopment_DoesNotWarn()
    {
        var env = DevelopmentEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Off });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options);

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_PermissiveMode_InProduction_WithSigner_DoesNotWarn()
    {
        var env = ProductionEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Permissive });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options, SignerStub());

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_StrictMode_InProduction_WithSigner_DoesNotWarn()
    {
        var env = ProductionEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Strict });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options, SignerStub());

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_PermissiveMode_NoSigner_LogsSignerWarning()
    {
        // Pre-nuget.org-Audit (2026-05-26) Security INFO: if integrity is configured to verify but
        // no IBusEnvelopeSigner is registered, the verifier silently returns Skipped — the contract
        // is no-op. The probe surfaces this misconfiguration at host start.
        var env = DevelopmentEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Permissive });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options, signer: null);

        await sut.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("IBusEnvelopeSigner"));
    }

    [Fact]
    public async Task StartAsync_StrictMode_NoSigner_LogsSignerWarning()
    {
        var env = ProductionEnvironment();
        var options = Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Strict });
        var logger = new RecordingLogger();
        var sut = new BusEnvelopeIntegrityStartupProbe(logger, env, options, signer: null);

        await sut.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("IBusEnvelopeSigner"));
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutError()
    {
        var sut = new BusEnvelopeIntegrityStartupProbe(
            NullLogger<BusEnvelopeIntegrityStartupProbe>.Instance,
            ProductionEnvironment(),
            Options.Create(new BusEnvelopeIntegrityOptions { Mode = BusEnvelopeIntegrityMode.Strict }),
            SignerStub());

        var exception = await Record.ExceptionAsync(async () => await sut.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    private static IBusEnvelopeSigner SignerStub() => new Mock<IBusEnvelopeSigner>().Object;

    private static IHostEnvironment ProductionEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        return env.Object;
    }

    private static IHostEnvironment DevelopmentEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return env.Object;
    }

    private sealed class RecordingLogger : ILogger<BusEnvelopeIntegrityStartupProbe>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
