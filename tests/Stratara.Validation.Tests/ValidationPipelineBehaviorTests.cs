using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Validation;
using Stratara.Validation;
using OpenTelemetry.Trace;
using Xunit;

namespace Stratara.Validation.Tests;

public class ValidationPipelineBehaviorTests
{
    private static ValidationPipelineBehavior<EchoQuery, string> ResultBehavior(params IValidator<EchoQuery>[] validators)
        => new(validators, NullLogger<ValidationPipelineBehavior<EchoQuery, string>>.Instance);

    [Fact]
    public async Task Valid_CallsNext_AndReturnsResult()
    {
        var behavior = ResultBehavior(new PassValidator());
        var handlerInvoked = false;

        var result = await behavior.HandleAsync(new EchoQuery("ok"), () =>
        {
            handlerInvoked = true;
            return Task.FromResult("handled");
        }, CancellationToken.None);

        Assert.True(handlerInvoked);
        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task Invalid_Error_Throws_AndHandlerNotInvoked()
    {
        var behavior = ResultBehavior(new FailValidator("Name", "Name is required."));
        var handlerInvoked = false;

        await Assert.ThrowsAsync<StrataraValidationException>(() =>
            behavior.HandleAsync(new EchoQuery("x"), () =>
            {
                handlerInvoked = true;
                return Task.FromResult("handled");
            }, CancellationToken.None));

        Assert.False(handlerInvoked);
    }

    [Fact]
    public async Task MultipleValidators_AggregateFailures()
    {
        var behavior = ResultBehavior(
            new FailValidator("A", "A bad"),
            new FailValidator("B", "B bad"));

        var ex = await Assert.ThrowsAsync<StrataraValidationException>(() =>
            behavior.HandleAsync(new EchoQuery("x"), () => Task.FromResult("handled"), CancellationToken.None));

        Assert.Equal(2, ex.Failures.Count);
        Assert.Contains(ex.Failures, f => f.PropertyName == "A");
        Assert.Contains(ex.Failures, f => f.PropertyName == "B");
    }

    [Fact]
    public async Task WarningAndInfo_DoNotBlock_HandlerInvoked()
    {
        var behavior = ResultBehavior(
            new SeverityValidator("W", "warn", ValidationSeverity.Warning),
            new SeverityValidator("I", "info", ValidationSeverity.Info));
        var handlerInvoked = false;

        var result = await behavior.HandleAsync(new EchoQuery("x"), () =>
        {
            handlerInvoked = true;
            return Task.FromResult("handled");
        }, CancellationToken.None);

        Assert.True(handlerInvoked);
        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task MixedSeverities_OnlyErrorBlocks_ExceptionCarriesErrorsOnly()
    {
        var behavior = ResultBehavior(
            new SeverityValidator("W", "warn", ValidationSeverity.Warning),
            new SeverityValidator("E", "err", ValidationSeverity.Error));

        var ex = await Assert.ThrowsAsync<StrataraValidationException>(() =>
            behavior.HandleAsync(new EchoQuery("x"), () => Task.FromResult("handled"), CancellationToken.None));

        Assert.Single(ex.Failures);
        Assert.Equal("E", ex.Failures[0].PropertyName);
    }

    [Fact]
    public async Task NoValidators_PassesThrough()
    {
        var behavior = ResultBehavior();
        var result = await behavior.HandleAsync(new EchoQuery("x"), () => Task.FromResult("handled"), CancellationToken.None);
        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task VoidRequest_Invalid_Throws_AndHandlerNotInvoked()
    {
        var behavior = new ValidationPipelineBehavior<EchoCommand>(
            [new FailVoidValidator()],
            NullLogger<ValidationPipelineBehavior<EchoCommand>>.Instance);
        var handlerInvoked = false;

        await Assert.ThrowsAsync<StrataraValidationException>(() =>
            behavior.HandleAsync(new EchoCommand(), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.False(handlerInvoked);
    }

    [Fact]
    public async Task ThroughMediator_ValidationRunsBeforeHandler()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(log);
        services.AddSingleton(TracerProvider.Default.GetTracer("test"));
        services.AddMediator();
        services.AddStrataraValidation();
        services.AddSingleton<IValidator<EchoQuery>>(new FailValidator("Name", "required"));
        services.AddScoped<IQueryHandler<EchoQuery, string>, RecordingHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<StrataraValidationException>(() =>
            mediator.HandleAsync(new EchoQuery("x")));

        Assert.DoesNotContain("handler", log);
    }

    [Fact]
    public void AddValidatorsFromAssemblyContaining_RegistersConcreteValidators()
    {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssemblyContaining<ValidationPipelineBehaviorTests>();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IValidator<EchoQuery>) &&
            d.ImplementationType == typeof(DiscoverableEchoValidator));
    }

    public sealed record EchoQuery(string Message) : IRequest<string>;

    public sealed record EchoCommand : ICommand;

    private sealed class RecordingHandler(List<string> log) : IQueryHandler<EchoQuery, string>
    {
        public Task<string> HandleAsync(EchoQuery request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            return Task.FromResult(request.Message);
        }
    }

    // Discovered by AddValidatorsFromAssemblyContaining — must be public + non-abstract.
    public sealed class DiscoverableEchoValidator : IValidator<EchoQuery>
    {
        public ValueTask<ValidationResult> ValidateAsync(EchoQuery instance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ValidationResult.Success);
    }

    private sealed class PassValidator : IValidator<EchoQuery>
    {
        public ValueTask<ValidationResult> ValidateAsync(EchoQuery instance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ValidationResult.Success);
    }

    private sealed class FailValidator(string property, string message) : IValidator<EchoQuery>
    {
        public ValueTask<ValidationResult> ValidateAsync(EchoQuery instance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ValidationResult([new ValidationFailure(property, message)]));
    }

    private sealed class SeverityValidator(string property, string message, ValidationSeverity severity) : IValidator<EchoQuery>
    {
        public ValueTask<ValidationResult> ValidateAsync(EchoQuery instance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ValidationResult([new ValidationFailure(property, message, Severity: severity)]));
    }

    private sealed class FailVoidValidator : IValidator<EchoCommand>
    {
        public ValueTask<ValidationResult> ValidateAsync(EchoCommand instance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ValidationResult([new ValidationFailure("X", "bad")]));
    }
}
