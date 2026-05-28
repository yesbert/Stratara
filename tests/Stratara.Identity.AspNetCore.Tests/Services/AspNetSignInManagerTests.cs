using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Identity.AspNetCore.Resources;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Identity.Core.Models;

namespace Stratara.Identity.AspNetCore.Tests.Services;

public class AspNetSignInManagerTests
{
    public sealed class TestUser : IdentityUser;

    private static Mock<UserManager<TestUser>> MockUserManager()
    {
        var store = new Mock<IUserStore<TestUser>>();
        return new Mock<UserManager<TestUser>>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<TestUser>>(),
            Array.Empty<IPasswordValidator<TestUser>>(),
            null!, null!, null!, null!);
    }

    private static Mock<SignInManager<TestUser>> MockSignInManager(UserManager<TestUser> userManager)
    {
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<TestUser>>();
        var options = new Mock<IOptions<IdentityOptions>>();
        options.Setup(o => o.Value).Returns(new IdentityOptions());
        var logger = new Mock<ILogger<SignInManager<TestUser>>>();
        var schemes = new Mock<IAuthenticationSchemeProvider>();
        var confirmation = new Mock<IUserConfirmation<TestUser>>();
        return new Mock<SignInManager<TestUser>>(
            userManager, contextAccessor.Object, claimsFactory.Object, options.Object,
            logger.Object, schemes.Object, confirmation.Object);
    }

    private static IStringLocalizer<IdentityResources> CreateLocalizer()
    {
        var localizer = new Mock<IStringLocalizer<IdentityResources>>();
        localizer.Setup(l => l[It.IsAny<string>()])
            .Returns<string>(key => new LocalizedString(key, $"localized:{key}"));
        return localizer.Object;
    }

    [Fact]
    public async Task PasswordSignInAsync_NotAllowed_ReturnsInvalidCredentialsMessage()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.PasswordSignInAsync("user@example.com", "pw", false, false))
            .ReturnsAsync(SignInResult.NotAllowed);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.PasswordSignInAsync("user@example.com", "pw", false, false);

        Assert.False(result.Succeeded);
        Assert.True(result.IsNotAllowed);
        Assert.Equal("localized:Identity.SignIn.InvalidCredentials", result.LoginFailureMessage);
    }

    [Fact]
    public async Task PasswordSignInAsync_InvalidCredentials_ReturnsSameMessageAsNotAllowed()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.PasswordSignInAsync("user@example.com", "pw", false, false))
            .ReturnsAsync(SignInResult.Failed);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.PasswordSignInAsync("user@example.com", "pw", false, false);

        Assert.False(result.Succeeded);
        Assert.False(result.IsNotAllowed);
        Assert.Equal("localized:Identity.SignIn.InvalidCredentials", result.LoginFailureMessage);
    }

    [Fact]
    public async Task PasswordSignInAsync_LockedOut_ReturnsLockoutMessage()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.PasswordSignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(SignInResult.LockedOut);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.PasswordSignInAsync("user@example.com", "pw", false, false);

        Assert.False(result.Succeeded);
        Assert.True(result.IsLockedOut);
        Assert.Equal("localized:Identity.SignIn.Lockout", result.LoginFailureMessage);
    }

    [Fact]
    public async Task PasswordSignInAsync_RequiresTwoFactor_ReturnsTwoFactorWithoutMessage()
    {
        var userManager = MockUserManager();
        userManager.Setup(u => u.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((TestUser?)null);
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.PasswordSignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(SignInResult.TwoFactorRequired);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.PasswordSignInAsync("user@example.com", "pw", false, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.LoginFailureMessage);
    }

    [Fact]
    public async Task PasswordSignInAsync_Success_ReturnsSuccessWithUserId()
    {
        var userId = Guid.NewGuid();
        var user = new TestUser { Id = userId.ToString(), Email = "u@example.com" };
        var userManager = MockUserManager();
        userManager.Setup(u => u.FindByEmailAsync("u@example.com")).ReturnsAsync(user);
        userManager.Setup(u => u.GetUserIdAsync(user)).ReturnsAsync(userId.ToString());
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.PasswordSignInAsync("u@example.com", "pw", false, false))
            .ReturnsAsync(SignInResult.Success);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.PasswordSignInAsync("u@example.com", "pw", false, false);

        Assert.True(result.Succeeded);
        Assert.Equal(userId, result.UserId);
        Assert.Null(result.LoginFailureMessage);
    }

    [Fact]
    public async Task PasswordSignInAsync_HonorsCancellationToken()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await sut.PasswordSignInAsync("u@example.com", "pw", false, false, cts.Token));
    }

    [Fact]
    public async Task TwoFactorAuthenticatorSignInAsync_LockedOut_ReturnsLockoutMessage()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.TwoFactorAuthenticatorSignInAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(SignInResult.LockedOut);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.TwoFactorAuthenticatorSignInAsync("000000", false, false);

        Assert.True(result.IsLockedOut);
        Assert.Equal("localized:Identity.SignIn.Lockout", result.LoginFailureMessage);
    }

    [Fact]
    public async Task TwoFactorAuthenticatorSignInAsync_Failed_ReturnsInvalidTwoFactorMessage()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.TwoFactorAuthenticatorSignInAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(SignInResult.Failed);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.TwoFactorAuthenticatorSignInAsync("bad", false, false);

        Assert.False(result.Succeeded);
        Assert.Equal("localized:Identity.SignIn.InvalidTwoFactor", result.LoginFailureMessage);
    }

    [Fact]
    public async Task TwoFactorRecoveryCodeSignInAsync_Failed_ReturnsInvalidRecoveryCodeMessage()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.TwoFactorRecoveryCodeSignInAsync(It.IsAny<string>()))
            .ReturnsAsync(SignInResult.Failed);

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        var result = await sut.TwoFactorRecoveryCodeSignInAsync("bad-recovery");

        Assert.False(result.Succeeded);
        Assert.Equal("localized:Identity.SignIn.InvalidRecoveryCode", result.LoginFailureMessage);
    }

    [Fact]
    public async Task RefreshSignInAsync_NotSupported_ThrowsNotSupportedException()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await sut.RefreshSignInAsync(null!));
    }

    [Fact]
    public async Task SignOutAsync_DelegatesToInnerSignInManager()
    {
        var userManager = MockUserManager();
        var signInManager = MockSignInManager(userManager.Object);
        signInManager.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask).Verifiable();

        var sut = new AspNetSignInManager<TestUser>(signInManager.Object, userManager.Object, CreateLocalizer());

        await sut.SignOutAsync();

        signInManager.Verify(s => s.SignOutAsync(), Times.Once);
    }
}
