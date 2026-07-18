using Stratara.Infrastructure.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

namespace Stratara.Infrastructure.Tests.Authorization;

public class AuthorizingCommandOutboxDispatcherPermissionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private readonly Mock<ICommandOutboxDispatcher> _innerMock = new();
    private readonly Mock<IAuthorizationProvider> _authProviderMock = new();
    private readonly Mock<IPermissionResolver> _resolverMock = new();
    private readonly Mock<ISessionContextProvider> _sessionMock = new();

    public AuthorizingCommandOutboxDispatcherPermissionTests()
    {
        _sessionMock.SetupGet(s => s.Current).Returns(
            new SessionContext("corr", null, null, TenantId, UserId, TenantId, UserId));
    }

    [RequirePermission("sims.write")]
    private sealed record GuardedCommand : ICommand;

    private sealed record OpenCommand : ICommand;

    private AuthorizingCommandOutboxDispatcher CreateDispatcher() =>
        new(_innerMock.Object, _authProviderMock.Object, _resolverMock.Object, _sessionMock.Object);

    [Fact]
    public async Task Held_permission_enqueues()
    {
        _resolverMock
            .Setup(r => r.ResolvePermissionsAsync(UserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["sims.write"], StringComparer.Ordinal));

        await CreateDispatcher().EnqueueCommandAsync(new GuardedCommand());

        _innerMock.Verify(i => i.EnqueueCommandAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_permission_short_circuits_before_the_inner_dispatcher()
    {
        _resolverMock
            .Setup(r => r.ResolvePermissionsAsync(UserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<PermissionAuthorizationException>(() =>
            CreateDispatcher().EnqueueCommandAsync(new GuardedCommand()));

        Assert.Equal("sims.write", ex.RequiredPermission);
        _innerMock.Verify(
            i => i.EnqueueCommandAsync(It.IsAny<GuardedCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Unguarded_commands_never_touch_the_resolver()
    {
        await CreateDispatcher().EnqueueCommandAsync(new OpenCommand());

        _resolverMock.Verify(
            r => r.ResolvePermissionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
