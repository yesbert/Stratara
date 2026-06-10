using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Trace;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Mediator.Multitenancy;
using Xunit;

namespace Stratara.Infrastructure.Tests.Multitenancy;

public class TenantIsolationBehaviorTests
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static SessionContext Session(Guid actorTenantId, Guid dataOwnerTenantId) =>
        new("corr", null, null, actorTenantId, Guid.NewGuid(), dataOwnerTenantId, null);

    private static TenantIsolationBehavior<ScopedQuery, string> ResultBehavior(
        SessionContext? session,
        ICrossTenantAuthorizer authorizer,
        TenantIsolationMode mode) =>
        new(
            new FakeSessionContextProvider(session),
            authorizer,
            new TenantIsolationOptions { Mode = mode },
            NullLogger<TenantIsolationBehavior<ScopedQuery, string>>.Instance);

    [Fact]
    public async Task NonTenantScopedRequest_PassesThrough()
    {
        var behavior = new TenantIsolationBehavior<PlainQuery, string>(
            new FakeSessionContextProvider(Session(TenantA, TenantA)),
            new DenyAuthorizer(),
            new TenantIsolationOptions { Mode = TenantIsolationMode.Strict },
            NullLogger<TenantIsolationBehavior<PlainQuery, string>>.Instance);

        var result = await behavior.HandleAsync(new PlainQuery(), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task MatchingTenant_Default_PassesThrough()
    {
        var behavior = ResultBehavior(Session(TenantA, TenantA), new DenyAuthorizer(), TenantIsolationMode.Default);

        var result = await behavior.HandleAsync(new ScopedQuery(TenantA), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task MismatchedTenant_Default_Rejected_AndHandlerNotInvoked()
    {
        var behavior = ResultBehavior(Session(TenantA, TenantA), new DenyAuthorizer(), TenantIsolationMode.Default);
        var handlerInvoked = false;

        var ex = await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            behavior.HandleAsync(new ScopedQuery(TenantB), () =>
            {
                handlerInvoked = true;
                return Task.FromResult("handled");
            }, CancellationToken.None));

        Assert.False(handlerInvoked);
        Assert.Equal(TenantB, ex.RequestedTenantId);
        Assert.Equal(TenantA, ex.SessionTenantId);
    }

    [Fact]
    public async Task CrossTenantAdmin_Default_PassesThrough_WhenSubjectPromoted()
    {
        // PlatformAdmin: actor home = A, the endpoint promoted the session data-owner to B, request targets B.
        var behavior = ResultBehavior(Session(TenantA, TenantB), new DenyAuthorizer(), TenantIsolationMode.Default);

        var result = await behavior.HandleAsync(new ScopedQuery(TenantB), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task CrossTenant_Strict_DenyAll_Rejected()
    {
        var behavior = ResultBehavior(Session(TenantA, TenantB), new DenyAuthorizer(), TenantIsolationMode.Strict);

        await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            behavior.HandleAsync(new ScopedQuery(TenantB), () => Task.FromResult("handled"), CancellationToken.None));
    }

    [Fact]
    public async Task CrossTenant_Strict_AuthorizerAllows_PassesThrough()
    {
        var behavior = ResultBehavior(Session(TenantA, TenantB), new AllowAuthorizer(), TenantIsolationMode.Strict);

        var result = await behavior.HandleAsync(new ScopedQuery(TenantB), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task SameTenant_Strict_DoesNotConsultAuthorizer()
    {
        var authorizer = new RecordingAuthorizer(allow: false);
        var behavior = ResultBehavior(Session(TenantA, TenantA), authorizer, TenantIsolationMode.Strict);

        var result = await behavior.HandleAsync(new ScopedQuery(TenantA), () => Task.FromResult("handled"), CancellationToken.None);

        Assert.Equal("handled", result);
        Assert.False(authorizer.WasConsulted);
    }

    [Fact]
    public async Task NoSession_Rejected()
    {
        var behavior = ResultBehavior(null, new AllowAuthorizer(), TenantIsolationMode.Default);

        await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            behavior.HandleAsync(new ScopedQuery(TenantA), () => Task.FromResult("handled"), CancellationToken.None));
    }

    [Fact]
    public async Task VoidCommand_MismatchedTenant_Rejected()
    {
        var behavior = new TenantIsolationBehavior<ScopedCommand>(
            new FakeSessionContextProvider(Session(TenantA, TenantA)),
            new DenyAuthorizer(),
            new TenantIsolationOptions { Mode = TenantIsolationMode.Default },
            NullLogger<TenantIsolationBehavior<ScopedCommand>>.Instance);
        var handlerInvoked = false;

        await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            behavior.HandleAsync(new ScopedCommand(TenantB), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.False(handlerInvoked);
    }

    [Fact]
    public async Task ThroughMediator_Strict_CustomAuthorizerGrantsCrossTenant()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddSingleton(TracerProvider.Default.GetTracer("test"));
        services.AddSingleton<ISessionContextProvider>(new FakeSessionContextProvider(Session(TenantA, TenantB)));
        services.AddSingleton<ICrossTenantAuthorizer, AllowAuthorizer>();
        services.AddMediator();
        services.AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict);
        services.AddScoped<IQueryHandler<ScopedQuery, string>, ScopedQueryHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.HandleAsync(new ScopedQuery(TenantB));

        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task ThroughMediator_Strict_DefaultDenyAll_RejectsCrossTenant()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TracerProvider.Default.GetTracer("test"));
        services.AddSingleton<ISessionContextProvider>(new FakeSessionContextProvider(Session(TenantA, TenantB)));
        services.AddMediator();
        services.AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict);
        services.AddScoped<IQueryHandler<ScopedQuery, string>, ScopedQueryHandler>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<TenantAccessDeniedException>(() => mediator.HandleAsync(new ScopedQuery(TenantB)));
    }

    public sealed record ScopedQuery(Guid TenantId) : IRequest<string>, ITenantScopedRequest;

    public sealed record ScopedCommand(Guid TenantId) : ICommand, ITenantScopedRequest;

    public sealed record PlainQuery : IRequest<string>;

    private sealed class ScopedQueryHandler : IQueryHandler<ScopedQuery, string>
    {
        public Task<string> HandleAsync(ScopedQuery request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }

    private sealed class FakeSessionContextProvider(SessionContext? current) : ISessionContextProvider
    {
        public SessionContext? Current { get; private set; } = current;
        public void Clear() => Current = null;
        public void Set(SessionContext context) => Current = context;
    }

    private sealed class DenyAuthorizer : ICrossTenantAuthorizer
    {
        public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class AllowAuthorizer : ICrossTenantAuthorizer
    {
        public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class RecordingAuthorizer(bool allow) : ICrossTenantAuthorizer
    {
        public bool WasConsulted { get; private set; }

        public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken cancellationToken = default)
        {
            WasConsulted = true;
            return ValueTask.FromResult(allow);
        }
    }
}
