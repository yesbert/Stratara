using Stratara.Infrastructure.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.Authorization;

public class AuthorizingCommandOutboxDispatcherTests
{
    private readonly Mock<ICommandOutboxDispatcher> _innerMock = new();
    private readonly Mock<IAuthorizationProvider> _authProviderMock = new();
    private readonly AuthorizingCommandOutboxDispatcher _dispatcher;

    public AuthorizingCommandOutboxDispatcherTests()
    {
        _dispatcher = new AuthorizingCommandOutboxDispatcher(_innerMock.Object, _authProviderMock.Object);
    }

    [RequireRole("Admin")]
    private sealed record AdminCommand : ICommand;

    [RequireRole("Admin")]
    [RequireRole("SuperAdmin")]
    private sealed record MultiRoleCommand : ICommand;

    private sealed record OpenCommand : ICommand;

    [Fact]
    public async Task EnqueueCommandAsync_Authorized_DelegatesToInner()
    {
        var command = new AdminCommand();
        var expectedId = Guid.NewGuid();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _innerMock.Setup(i => i.EnqueueCommandAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(expectedId);

        var result = await _dispatcher.EnqueueCommandAsync(command);

        Assert.Equal(expectedId, result);
        _innerMock.Verify(i => i.EnqueueCommandAsync(command, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_Unauthorized_ThrowsAuthorizationException()
    {
        var command = new AdminCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _dispatcher.EnqueueCommandAsync(command));

        Assert.Equal("Admin", ex.RequiredRole);
    }

    [Fact]
    public async Task EnqueueCommandAsync_NoRequireRole_PassesThrough()
    {
        var command = new OpenCommand();
        var expectedId = Guid.NewGuid();
        _innerMock.Setup(i => i.EnqueueCommandAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(expectedId);

        var result = await _dispatcher.EnqueueCommandAsync(command);

        Assert.Equal(expectedId, result);
        _authProviderMock.Verify(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueCommandAsync_MultipleRoles_ChecksAll()
    {
        var command = new MultiRoleCommand();
        var expectedId = Guid.NewGuid();
        _authProviderMock.Setup(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _innerMock.Setup(i => i.EnqueueCommandAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(expectedId);

        var result = await _dispatcher.EnqueueCommandAsync(command);

        Assert.Equal(expectedId, result);
        _authProviderMock.Verify(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>()), Times.Once);
        _authProviderMock.Verify(a => a.IsInRoleAsync("SuperAdmin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueCommandAsync_MultipleRoles_FailsOnFirst()
    {
        var command = new MultiRoleCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _dispatcher.EnqueueCommandAsync(command));

        Assert.Equal("Admin", ex.RequiredRole);
        _innerMock.Verify(i => i.EnqueueCommandAsync(It.IsAny<MultiRoleCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueOutboxEntriesAsync_AlwaysPassesThrough()
    {
        var entries = new List<OutboxEntry>();

        await _dispatcher.EnqueueOutboxEntriesAsync(entries);

        _innerMock.Verify(i => i.EnqueueOutboxEntriesAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
        _authProviderMock.Verify(a => a.IsInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueCommandAsync_AuthorizationException_ContainsRequiredRole()
    {
        var command = new AdminCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<AuthorizationException>(() =>
            _dispatcher.EnqueueCommandAsync(command));

        Assert.Equal("Admin", ex.RequiredRole);
        Assert.Contains("Admin", ex.Message);
    }

    [Fact]
    public async Task EnqueueCommandAsync_ChecksRoleViaProvider()
    {
        var command = new AdminCommand();
        _authProviderMock.Setup(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _innerMock.Setup(i => i.EnqueueCommandAsync(command, It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());

        await _dispatcher.EnqueueCommandAsync(command);

        _authProviderMock.Verify(a => a.IsInRoleAsync("Admin", It.IsAny<CancellationToken>()), Times.Once);
    }
}
