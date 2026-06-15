using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Trace;
using Polly;
using Polly.Retry;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Resilience;
using Stratara.Resilience;
using Xunit;

namespace Stratara.Infrastructure.Tests.Resilience;

public class ResiliencePipelineBehaviorTests
{
    private const string TestRetryPipeline = "test-retry";

    private sealed class AttemptCounter
    {
        public int Count { get; set; }
    }

    private sealed record RetryQuery : IQuery<string>, IResilientRequest
    {
        public string ResiliencePipelineName => TestRetryPipeline;
    }

    private sealed record PlainQuery : IQuery<string>;

    private sealed record ConcurrencyQuery : IQuery<string>, IResilientRequest
    {
        public string ResiliencePipelineName => ResilienceNames.ConcurrencyConflict;
    }

    private sealed class RetryUntilThirdHandler(AttemptCounter counter) : IQueryHandler<RetryQuery, string>
    {
        public Task<string> HandleAsync(RetryQuery request, CancellationToken cancellationToken)
        {
            counter.Count++;
            return counter.Count < 3
                ? throw new InvalidOperationException("transient")
                : Task.FromResult("ok");
        }
    }

    private sealed class AlwaysThrowsPlainHandler(AttemptCounter counter) : IQueryHandler<PlainQuery, string>
    {
        public Task<string> HandleAsync(PlainQuery request, CancellationToken cancellationToken)
        {
            counter.Count++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ConcurrencyThenSuccessHandler(AttemptCounter counter) : IQueryHandler<ConcurrencyQuery, string>
    {
        public Task<string> HandleAsync(ConcurrencyQuery request, CancellationToken cancellationToken)
        {
            counter.Count++;
            return counter.Count < 3
                ? throw new ConcurrencyConflictException()
                : Task.FromResult("ok");
        }
    }

    private sealed class AlwaysThrowsConcurrencyQueryHandler(AttemptCounter counter) : IQueryHandler<ConcurrencyQuery, string>
    {
        public Task<string> HandleAsync(ConcurrencyQuery request, CancellationToken cancellationToken)
        {
            counter.Count++;
            throw new InvalidOperationException("not a concurrency conflict");
        }
    }

    private static ServiceProvider BuildProvider(AttemptCounter counter, Action<IServiceCollection> registerHandler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddSingleton(TracerProvider.Default.GetTracer("test"));
        services.AddSingleton(counter);
        services.AddMediator();
        services.AddResiliencePipelines();
        services.AddResiliencePipeline(TestRetryPipeline, builder => builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.Zero,
            BackoffType = DelayBackoffType.Constant
        }));
        services.AddStrataraResilienceBehavior();
        registerHandler(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task MarkedRequest_IsRetried_UntilHandlerSucceeds()
    {
        var counter = new AttemptCounter();
        await using var sp = BuildProvider(counter, s =>
            s.AddScoped<IQueryHandler<RetryQuery, string>, RetryUntilThirdHandler>());
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.HandleAsync(new RetryQuery());

        Assert.Equal("ok", result);
        Assert.Equal(3, counter.Count);
    }

    [Fact]
    public async Task UnmarkedRequest_IsNotRetried()
    {
        var counter = new AttemptCounter();
        await using var sp = BuildProvider(counter, s =>
            s.AddScoped<IQueryHandler<PlainQuery, string>, AlwaysThrowsPlainHandler>());
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.HandleAsync(new PlainQuery()));
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task ConcurrencyConflictPipeline_RetriesOnConflict()
    {
        var counter = new AttemptCounter();
        await using var sp = BuildProvider(counter, s =>
            s.AddScoped<IQueryHandler<ConcurrencyQuery, string>, ConcurrencyThenSuccessHandler>());
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.HandleAsync(new ConcurrencyQuery());

        Assert.Equal("ok", result);
        Assert.Equal(3, counter.Count);
    }

    [Fact]
    public async Task ConcurrencyConflictPipeline_DoesNotRetryOtherExceptions()
    {
        var counter = new AttemptCounter();
        await using var sp = BuildProvider(counter, s =>
            s.AddScoped<IQueryHandler<ConcurrencyQuery, string>, AlwaysThrowsConcurrencyQueryHandler>());
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.HandleAsync(new ConcurrencyQuery()));
        Assert.Equal(1, counter.Count);
    }
}
