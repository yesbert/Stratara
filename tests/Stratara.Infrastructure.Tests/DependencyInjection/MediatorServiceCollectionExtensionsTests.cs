using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class MediatorServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMediator_Registers_IMediator_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        var descriptor = services.Single(d => d.ServiceType == typeof(IMediator));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddMediator_ResolvesToConcreteMediator_WhenDependenciesPresent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Tracer>(TracerProvider.Default.GetTracer("test"));
        services.AddMediator();

        var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        Assert.NotNull(mediator);
        Assert.Equal("Stratara.Mediator.Mediator", mediator.GetType().FullName);
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_OpenGeneric_RegistersAsScoped()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehaviorWithResult(typeof(TwoParamBehavior<,>));

        var descriptor = services.Single(d => d.ServiceType == typeof(IPipelineBehavior<,>));
        Assert.Equal(typeof(TwoParamBehavior<,>), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_ResolvesClosedGeneric_AtRuntime()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehaviorWithResult(typeof(TwoParamBehavior<,>));
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<IPipelineBehavior<TestQuery, string>>();

        Assert.IsType<TwoParamBehavior<TestQuery, string>>(resolved);
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_RejectsNonGenericTypes()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddPipelineBehaviorWithResult(typeof(NotGeneric)));
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_RejectsWrongArity()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddPipelineBehaviorWithResult(typeof(OneParamBehavior<>)));
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_RejectsNull()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddPipelineBehaviorWithResult(null!));
    }

    [Fact]
    public void AddPipelineBehavior_OpenGeneric_RegistersAsScoped()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehavior(typeof(OneParamBehavior<>));

        var descriptor = services.Single(d => d.ServiceType == typeof(IPipelineBehavior<>));
        Assert.Equal(typeof(OneParamBehavior<>), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddPipelineBehavior_ResolvesClosedGeneric_AtRuntime()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehavior(typeof(OneParamBehavior<>));
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<IPipelineBehavior<TestCommand>>();

        Assert.IsType<OneParamBehavior<TestCommand>>(resolved);
    }

    [Fact]
    public void AddPipelineBehavior_RejectsWrongArity()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddPipelineBehavior(typeof(TwoParamBehavior<,>)));
    }

    [Fact]
    public void AddPipelineBehavior_RejectsNull()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddPipelineBehavior(null!));
    }

    [Fact]
    public void AddCommandHandlersFromAssemblyContaining_RegistersAllClosedGenericHandlers_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddCommandHandlersFromAssemblyContaining<MediatorServiceCollectionExtensionsTests>();

        var descriptor = services.Single(d => d.ServiceType == typeof(ICommandHandler<TestCommand>));
        Assert.Equal(typeof(TestCommandHandler), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddQueryHandlersFromAssemblyContaining_RegistersAllClosedGenericHandlers_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddQueryHandlersFromAssemblyContaining<MediatorServiceCollectionExtensionsTests>();

        var descriptor = services.Single(d => d.ServiceType == typeof(IQueryHandler<TestQuery, string>));
        Assert.Equal(typeof(TestQueryHandler), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_CalledTwice_InstallsOnce()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehaviorWithResult(typeof(TwoParamBehavior<,>));
        services.AddPipelineBehaviorWithResult(typeof(TwoParamBehavior<,>));

        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<,>));
    }

    [Fact]
    public void AddPipelineBehavior_CalledTwice_InstallsOnce()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehavior(typeof(OneParamBehavior<>));
        services.AddPipelineBehavior(typeof(OneParamBehavior<>));

        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<>));
    }

    [Fact]
    public void AddPipelineBehaviorWithResult_DistinctBehaviorTypes_AreBothInstalled()
    {
        var services = new ServiceCollection();
        services.AddPipelineBehaviorWithResult(typeof(TwoParamBehavior<,>));
        services.AddPipelineBehaviorWithResult(typeof(CountingBehavior<,>));

        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IPipelineBehavior<,>)));
    }

    [Fact]
    public async Task TwiceRegisteredStage_RunsOncePerDispatchedRequest()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(log);
        services.AddSingleton(TracerProvider.Default.GetTracer("test"));
        services.AddMediator();
        services.AddPipelineBehaviorWithResult(typeof(CountingBehavior<,>));
        services.AddPipelineBehaviorWithResult(typeof(CountingBehavior<,>));
        services.AddScoped<IQueryHandler<TestQuery, string>, TestQueryHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new TestQuery());

        Assert.Single(log);
    }

    [Fact]
    public async Task AddMediator_Alone_ResolvesAndDispatches()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingQuery("hello"));

        Assert.Equal("pong:hello", result);
    }

    [Fact]
    public async Task AddMediator_WithoutHostTracer_EmitsDispatchSpansFromFrameworkSource()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();

        var spans = await CollectSpansAsync(ApplicationDiagnostics.Activity.SourceName, async () =>
        {
            using var scope = sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingQuery("x"));
        });

        Assert.Contains("Handle PingQuery", spans);
    }

    [Fact]
    public async Task AddMediator_HostTracerRegisteredBefore_IsUsedForDispatchSpans()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TracerProvider.Default.GetTracer("host-before"));
        services.AddMediator();
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();

        var spans = await CollectSpansAsync("host-before", async () =>
        {
            using var scope = sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingQuery("x"));
        });

        Assert.Contains("Handle PingQuery", spans);
    }

    [Fact]
    public async Task AddMediator_HostTracerRegisteredAfter_IsUsedForDispatchSpans()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddSingleton(TracerProvider.Default.GetTracer("host-after"));
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();

        var spans = await CollectSpansAsync("host-after", async () =>
        {
            using var scope = sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingQuery("x"));
        });

        Assert.Contains("Handle PingQuery", spans);
    }

    [Fact]
    public async Task AddAuthorizingMediator_WithoutAddMediatorOrHostTracer_ResolvesAndDispatches()
    {
        var services = new ServiceCollection();
        services.AddAuthorizingMediator<AllowEverythingAuthorizationProvider>();
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingQuery("auth"));

        Assert.Equal("pong:auth", result);
    }

    private static async Task<IReadOnlyList<string>> CollectSpansAsync(string sourceName, Func<Task> act)
    {
        var names = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => names.Add(activity.DisplayName),
        };
        ActivitySource.AddActivityListener(listener);

        await act();

        return names;
    }

    public sealed class AllowEverythingAuthorizationProvider : IAuthorizationProvider
    {
        public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    public sealed record PingQuery(string Payload) : IQuery<string>;

    public sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
    {
        public Task<string> HandleAsync(PingQuery request, CancellationToken cancellationToken) =>
            Task.FromResult($"pong:{request.Payload}");
    }

    public sealed record TestCommand : ICommand;

    public sealed record TestQuery : IRequest<string>;

    public sealed class TestCommandHandler : ICommandHandler<TestCommand>
    {
        public Task HandleAsync(TestCommand request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class TestQueryHandler : IQueryHandler<TestQuery, string>
    {
        public Task<string> HandleAsync(TestQuery request, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    public sealed class TwoParamBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
        where TRequest : IRequest<TResult>
    {
        public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken) =>
            next();
    }

    public sealed class OneParamBehavior<TRequest> : IPipelineBehavior<TRequest>
        where TRequest : IRequest
    {
        public Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken) =>
            next();
    }

    public sealed class CountingBehavior<TRequest, TResult>(List<string> log) : IPipelineBehavior<TRequest, TResult>
        where TRequest : IRequest<TResult>
    {
        public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
        {
            log.Add(typeof(TRequest).Name);
            return next();
        }
    }

    public sealed class NotGeneric { }
}
