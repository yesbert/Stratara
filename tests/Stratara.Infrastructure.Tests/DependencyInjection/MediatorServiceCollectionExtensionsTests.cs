using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;

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

    public sealed class NotGeneric { }
}
