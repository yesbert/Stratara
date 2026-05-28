using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;
using SutMediator = Stratara.Mediator.Mediator;

namespace Stratara.Infrastructure.Tests;

public class PipelineBehaviorTests
{
    private static readonly string[] ExpectedBehaviorOrder =
        ["first:before", "second:before", "handler", "second:after", "first:after"];

    private static Tracer NoopTracer() => TracerProvider.Default.GetTracer("test");

    [Fact]
    public async Task Behaviors_RunInRegistrationOrder_BeforeHandler_AndUnwindInReverseAfter()
    {
        var log = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, FirstBehavior>();
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, SecondBehavior>();
        services.AddSingleton<IQueryHandler<EchoQuery, string>, EchoQueryHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        var result = await mediator.HandleAsync(new EchoQuery("payload"));

        Assert.Equal("payload", result);
        Assert.Equal(ExpectedBehaviorOrder, log);
    }

    [Fact]
    public async Task Behavior_CanShortCircuit_HandlerNotInvoked()
    {
        var log = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, ShortCircuitBehavior>();
        services.AddSingleton<IQueryHandler<EchoQuery, string>, EchoQueryHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        var result = await mediator.HandleAsync(new EchoQuery("ignored"));

        Assert.Equal("short-circuited", result);
        Assert.DoesNotContain("handler", log);
    }

    [Fact]
    public async Task Behavior_Exception_Propagates_And_OuterBehavior_SeesIt()
    {
        var log = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, CatchingBehavior>();
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, ThrowingBehavior>();
        services.AddSingleton<IQueryHandler<EchoQuery, string>, EchoQueryHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        var result = await mediator.HandleAsync(new EchoQuery("x"));

        Assert.Equal("caught:boom", result);
        Assert.Contains("catching:caught", log);
        Assert.DoesNotContain("handler", log);
    }

    [Fact]
    public async Task Cancellation_PropagatesThroughBehaviors_AndThrows()
    {
        var log = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IPipelineBehavior<EchoQuery, string>, CancellationObservingBehavior>();
        services.AddSingleton<IQueryHandler<EchoQuery, string>, CancellationObservingHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            mediator.HandleAsync(new EchoQuery("ct"), cts.Token));

        Assert.Contains("behavior:cancelled", log);
    }

    [Fact]
    public async Task NoBehaviors_HandlerStillInvoked()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddSingleton<IQueryHandler<EchoQuery, string>, EchoQueryHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        var result = await mediator.HandleAsync(new EchoQuery("baseline"));

        Assert.Equal("baseline", result);
    }

    [Fact]
    public async Task VoidCommand_Behaviors_RunInOrder()
    {
        var log = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IPipelineBehavior<VoidCommand>, FirstVoidBehavior>();
        services.AddScoped<IPipelineBehavior<VoidCommand>, SecondVoidBehavior>();
        services.AddSingleton<ICommandHandler<VoidCommand>, VoidHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(NoopTracer(), sp);

        await mediator.HandleAsync(new VoidCommand());

        Assert.Equal(ExpectedBehaviorOrder, log);
    }

    private sealed record EchoQuery(string Message) : IRequest<string>;

    private sealed class EchoQueryHandler(List<string> log) : IQueryHandler<EchoQuery, string>
    {
        public Task<string> HandleAsync(EchoQuery request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            return Task.FromResult(request.Message);
        }
    }

    private sealed class FirstBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public async Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            log.Add("first:before");
            var result = await next();
            log.Add("first:after");
            return result;
        }
    }

    private sealed class SecondBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public async Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            log.Add("second:before");
            var result = await next();
            log.Add("second:after");
            return result;
        }
    }

    private sealed class ShortCircuitBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            log.Add("short-circuit");
            return Task.FromResult("short-circuited");
        }
    }

    private sealed class ThrowingBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            log.Add("throwing");
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CatchingBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public async Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            try
            {
                return await next();
            }
            catch (InvalidOperationException ex)
            {
                log.Add("catching:caught");
                return $"caught:{ex.Message}";
            }
        }
    }

    private sealed class CancellationObservingBehavior(List<string> log) : IPipelineBehavior<EchoQuery, string>
    {
        public async Task<string> HandleAsync(EchoQuery request, Func<Task<string>> next, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                log.Add("behavior:cancelled");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await next();
        }
    }

    private sealed class CancellationObservingHandler : IQueryHandler<EchoQuery, string>
    {
        public Task<string> HandleAsync(EchoQuery request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request.Message);
        }
    }

    private sealed record VoidCommand : ICommand;

    private sealed class VoidHandler(List<string> log) : ICommandHandler<VoidCommand>
    {
        public Task HandleAsync(VoidCommand request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            return Task.CompletedTask;
        }
    }

    private sealed class FirstVoidBehavior(List<string> log) : IPipelineBehavior<VoidCommand>
    {
        public async Task HandleAsync(VoidCommand request, Func<Task> next, CancellationToken cancellationToken)
        {
            log.Add("first:before");
            await next();
            log.Add("first:after");
        }
    }

    private sealed class SecondVoidBehavior(List<string> log) : IPipelineBehavior<VoidCommand>
    {
        public async Task HandleAsync(VoidCommand request, Func<Task> next, CancellationToken cancellationToken)
        {
            log.Add("second:before");
            await next();
            log.Add("second:after");
        }
    }
}
