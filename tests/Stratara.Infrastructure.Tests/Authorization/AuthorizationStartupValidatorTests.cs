using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Mediator.Authorization;

namespace Stratara.Infrastructure.Tests.Authorization;

public class AuthorizationStartupValidatorTests
{
    [Fact]
    public async Task StartAsync_NoMediatorRegistered_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var sut = new AuthorizationStartupValidator(sp);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_AuthorizingMediatorAndResolverRegistered_DoesNotThrow()
    {
        // The test assembly also contains [RequirePermission]-decorated types (see the synthetic
        // type below and AuthorizingMediatorPermissionTests), so the fully wired configuration
        // needs an IPermissionResolver alongside the authorizing mediator.
        var services = new ServiceCollection();
        services.AddScoped<IMediator>(_ => new Mock<IAuthorizingMediator>().Object);
        services.AddScoped<IPermissionResolver>(_ => new Mock<IPermissionResolver>().Object);
        var sp = services.BuildServiceProvider();

        var sut = new AuthorizationStartupValidator(sp);

        var exception = await Record.ExceptionAsync(async () => await sut.StartAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAsync_AuthorizingMediatorWithoutResolverButPermissionTypesExist_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMediator>(_ => new Mock<IAuthorizingMediator>().Object);
        var sp = services.BuildServiceProvider();

        var sut = new AuthorizationStartupValidator(sp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.StartAsync(CancellationToken.None));

        Assert.Contains("[RequirePermission]", ex.Message);
        Assert.Contains("IPermissionResolver", ex.Message);
    }

    [Fact]
    public async Task StartAsync_PlainMediatorAndRoleProtectedTypeExists_ThrowsInvalidOperation()
    {
        // The test assembly itself contains [RequireRole]-decorated types (AuthorizingMediatorTests,
        // AuthorizingCommandOutboxDispatcherTests, plus the type below) — the validator will discover
        // at least one of them when scanning AppDomain.CurrentDomain assemblies.
        var services = new ServiceCollection();
        services.AddScoped<IMediator>(_ => new Mock<IMediator>().Object);
        var sp = services.BuildServiceProvider();

        var sut = new AuthorizationStartupValidator(sp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.StartAsync(CancellationToken.None));

        Assert.Contains("[RequireRole]", ex.Message);
        Assert.Contains("IAuthorizingMediator", ex.Message);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutError()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var sut = new AuthorizationStartupValidator(sp);

        var exception = await Record.ExceptionAsync(async () => await sut.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [RequireRole("test-only-role")]
    private sealed record SyntheticRoleProtectedCommand;

    [RequirePermission("test-only.permission")]
    private sealed record SyntheticPermissionProtectedCommand;
}
