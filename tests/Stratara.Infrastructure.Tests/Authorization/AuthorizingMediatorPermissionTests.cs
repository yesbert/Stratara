using Stratara.Mediator.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

namespace Stratara.Infrastructure.Tests.Authorization;

public class AuthorizingMediatorPermissionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private readonly Mock<IMediator> _innerMock = new();
    private readonly Mock<IAuthorizationProvider> _authProviderMock = new();
    private readonly Mock<IPermissionResolver> _resolverMock = new();
    private readonly Mock<ISessionContextProvider> _sessionMock = new();

    public AuthorizingMediatorPermissionTests()
    {
        _sessionMock.SetupGet(s => s.Current).Returns(
            new SessionContext("corr", null, null, TenantId, UserId, TenantId, UserId));
    }

    private AuthorizingMediator CreateMediator(bool withResolver = true, bool withSession = true) =>
        new(
            _innerMock.Object,
            _authProviderMock.Object,
            withResolver ? _resolverMock.Object : null,
            withSession ? _sessionMock.Object : null);

    private void HoldPermissions(params string[] permissions) =>
        _resolverMock
            .Setup(r => r.ResolvePermissionsAsync(UserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(permissions, StringComparer.Ordinal));

    [RequirePermission("sims.read")]
    private sealed record ReadSimsQuery : IQuery<string>;

    [RequirePermission("sims.read")]
    [RequirePermission("sims.write")]
    private sealed record MutateSimsCommand : ICommand;

    [RequireRole("Admin")]
    [RequirePermission("sims.read")]
    private sealed record RoleAndPermissionQuery : IQuery<int>;

    private sealed record OpenQuery : IQuery<string>;

    [Fact]
    public async Task Held_permission_passes_through_to_the_handler()
    {
        HoldPermissions("sims.read");
        var query = new ReadSimsQuery();
        _innerMock.Setup(i => i.HandleAsync<string>(query, It.IsAny<CancellationToken>())).ReturnsAsync("ok");

        var result = await CreateMediator().HandleAsync<string>(query);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Missing_permission_throws_with_the_permission_name()
    {
        HoldPermissions();

        var ex = await Assert.ThrowsAsync<PermissionAuthorizationException>(() =>
            CreateMediator().HandleAsync<string>(new ReadSimsQuery()));

        Assert.Equal("sims.read", ex.RequiredPermission);
        Assert.IsAssignableFrom<AuthorizationException>(ex);
        Assert.Contains("sims.read", ex.Message);
        _innerMock.Verify(i => i.HandleAsync<string>(It.IsAny<IRequest<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Multiple_permission_attributes_are_anded()
    {
        HoldPermissions("sims.read");

        var ex = await Assert.ThrowsAsync<PermissionAuthorizationException>(() =>
            CreateMediator().HandleAsync(new MutateSimsCommand()));

        Assert.Equal("sims.write", ex.RequiredPermission);
    }

    [Fact]
    public async Task Roles_and_permissions_compose_on_the_same_request()
    {
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        HoldPermissions("sims.read");
        var query = new RoleAndPermissionQuery();
        _innerMock.Setup(i => i.HandleAsync<int>(query, It.IsAny<CancellationToken>())).ReturnsAsync(7);

        Assert.Equal(7, await CreateMediator().HandleAsync<int>(query));

        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<AuthorizationException>(() => CreateMediator().HandleAsync<int>(query));
    }

    [Fact]
    public async Task Unguarded_requests_never_touch_the_resolver()
    {
        var query = new OpenQuery();
        _innerMock.Setup(i => i.HandleAsync<string>(query, It.IsAny<CancellationToken>())).ReturnsAsync("open");

        await CreateMediator().HandleAsync<string>(query);

        _resolverMock.Verify(
            r => r.ResolvePermissionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Missing_resolver_on_a_guarded_request_fails_loud()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMediator(withResolver: false).HandleAsync<string>(new ReadSimsQuery()));

        Assert.Contains("IPermissionResolver", ex.Message);
    }

    [Fact]
    public async Task Missing_session_fails_closed()
    {
        _sessionMock.SetupGet(s => s.Current).Returns((SessionContext?)null);

        var ex = await Assert.ThrowsAsync<PermissionAuthorizationException>(() =>
            CreateMediator().HandleAsync<string>(new ReadSimsQuery()));

        Assert.Equal("sims.read", ex.RequiredPermission);
    }
}
