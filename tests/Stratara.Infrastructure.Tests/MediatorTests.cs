using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;
using SutMediator = Stratara.Mediator.Mediator;

namespace Stratara.Infrastructure.Tests;

public class MediatorTests
{
    private static Tracer CreateNoopTracer() => TracerProvider.Default.GetTracer("test");

    [Fact]
    public async Task HandleAsync_Query_Invokes_Handler_And_Returns_Result()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        var result = await mediator.HandleAsync(new PingQuery("hello"));

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task HandleAsync_Command_Invokes_Handler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<PingCommand>, PingCommandHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await mediator.HandleAsync(new PingCommand("pong"));

        Assert.True(PingCommandHandler.Handled);
        PingCommandHandler.Handled = false;
    }

    [Fact]
    public async Task HandleAsync_CommandWithResult_Invokes_Handler_And_Returns_Result()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueryHandler<PingCommandWithResult, string>, PingCommandWithResultHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        var result = await mediator.HandleAsync(new PingCommandWithResult("with-result"));

        Assert.Equal("with-result", result);
    }

    [Fact]
    public async Task HandleAsync_Throws_When_Handler_Not_Found()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.HandleAsync(new PingQuery("x")));
    }

    [Fact]
    public async Task HandleAsync_Query_ThrowsOnNull()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await Assert.ThrowsAsync<ArgumentNullException>(() => mediator.HandleAsync<string>(null!));
    }

    [Fact]
    public async Task HandleAsync_Command_ThrowsOnNull()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await Assert.ThrowsAsync<ArgumentNullException>(() => mediator.HandleAsync((PingCommand)null!));
    }

    [Fact]
    public async Task HandleAsync_NonCommandBase_Invokes_Handler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<NonCommandBaseRequest>, NonCommandBaseHandler>();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await mediator.HandleAsync(new NonCommandBaseRequest());

        Assert.True(NonCommandBaseHandler.Handled);
        NonCommandBaseHandler.Handled = false;
    }

    [Fact]
    public async Task HandleAsync_Command_Throws_When_Command_Handler_Not_Found()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var mediator = new SutMediator(CreateNoopTracer(), sp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.HandleAsync(new PingCommand("x")));
    }

    private sealed record PingQuery(string Message) : IRequest<string>;

    private sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
    {
        public Task<string> HandleAsync(PingQuery request, CancellationToken cancellationToken)
            => Task.FromResult(request.Message);
    }

    private sealed record PingCommand(string Message) : ICommand;

    private sealed record PingCommandWithResult(string Message) : ICommand<string>;

    private sealed class PingCommandWithResultHandler : IQueryHandler<PingCommandWithResult, string>
    {
        public Task<string> HandleAsync(PingCommandWithResult request, CancellationToken cancellationToken)
            => Task.FromResult(request.Message);
    }

    private sealed class PingCommandHandler : ICommandHandler<PingCommand>
    {
        public static bool Handled { get; set; }

        public Task HandleAsync(PingCommand request, CancellationToken cancellationToken)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }

    private sealed record NonCommandBaseRequest : IRequest;

    private sealed class NonCommandBaseHandler : ICommandHandler<NonCommandBaseRequest>
    {
        public static bool Handled { get; set; }

        public Task HandleAsync(NonCommandBaseRequest request, CancellationToken cancellationToken)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }
}
