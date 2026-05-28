using Stratara.Mediator.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.Authorization;

public class AuthorizingMediatorTests
{
    private readonly Mock<IMediator> _innerMock = new();
    private readonly Mock<IAuthorizationProvider> _authProviderMock = new();
    private readonly AuthorizingMediator _mediator;

    public AuthorizingMediatorTests()
    {
        _mediator = new AuthorizingMediator(_innerMock.Object, _authProviderMock.Object);
    }

    [RequireRole("Admin")]
    private sealed record AdminQuery : IQuery<string>;

    private sealed record OpenQuery : IQuery<string>;

    [RequireRole("Admin")]
    private sealed record AdminCommand : ICommand;

    private sealed record OpenCommand : ICommand;

    [RequireRole("Admin")]
    [RequireRole("SuperAdmin")]
    private sealed record MultiRoleQuery : IQuery<int>;

    [Fact]
    public async Task HandleAsync_Query_Authorized_ReturnsResult()
    {
        var query = new AdminQuery();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _innerMock.Setup(i => i.HandleAsync<string>(query, It.IsAny<CancellationToken>())).ReturnsAsync("result");

        var result = await _mediator.HandleAsync<string>(query);

        Assert.Equal("result", result);
    }

    [Fact]
    public async Task HandleAsync_Query_Unauthorized_ThrowsAuthorizationException()
    {
        var query = new AdminQuery();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _mediator.HandleAsync<string>(query));

        Assert.Equal("Admin", ex.RequiredRole);
    }

    [Fact]
    public async Task HandleAsync_Query_NoRequireRole_PassesThrough()
    {
        var query = new OpenQuery();
        _innerMock.Setup(i => i.HandleAsync<string>(query, It.IsAny<CancellationToken>())).ReturnsAsync("open");

        var result = await _mediator.HandleAsync<string>(query);

        Assert.Equal("open", result);
        _authProviderMock.Verify(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_Command_Authorized_Executes()
    {
        var command = new AdminCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _mediator.HandleAsync(command);

        _innerMock.Verify(i => i.HandleAsync(command, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_Command_Unauthorized_ThrowsAuthorizationException()
    {
        var command = new AdminCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _mediator.HandleAsync(command));

        Assert.Equal("Admin", ex.RequiredRole);
    }

    [Fact]
    public async Task HandleAsync_Command_NoRequireRole_PassesThrough()
    {
        var command = new OpenCommand();

        await _mediator.HandleAsync(command);

        _innerMock.Verify(i => i.HandleAsync(command, It.IsAny<CancellationToken>()), Times.Once);
        _authProviderMock.Verify(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_MultipleRoles_ChecksAll()
    {
        var query = new MultiRoleQuery();
        _authProviderMock.Setup(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _innerMock.Setup(i => i.HandleAsync<int>(query, It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await _mediator.HandleAsync<int>(query);

        Assert.Equal(42, result);
        _authProviderMock.Verify(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>()), Times.Once);
        _authProviderMock.Verify(a => a.IsInRoleAsync("SuperAdmin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AuthorizationException_ContainsRequiredRole()
    {
        var query = new AdminQuery();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _mediator.HandleAsync<string>(query));

        Assert.Equal("Admin", ex.RequiredRole);
        Assert.Contains("Admin", ex.Message);
    }
}
