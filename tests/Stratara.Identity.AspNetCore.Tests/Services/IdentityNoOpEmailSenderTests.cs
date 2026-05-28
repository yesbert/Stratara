using Microsoft.AspNetCore.Identity;
using Stratara.Identity.AspNetCore.Services;

namespace Stratara.Identity.AspNetCore.Tests.Services;

public class IdentityNoOpEmailSenderTests
{
    private sealed class TestUser : IdentityUser;

    [Fact]
    public async Task SendConfirmationLinkAsync_DoesNotThrow()
    {
        var sender = new IdentityNoOpEmailSender<TestUser>();

        var exception = await Record.ExceptionAsync(() =>
            sender.SendConfirmationLinkAsync(new TestUser(), "test@example.com", "https://example.test/confirm"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendPasswordResetLinkAsync_DoesNotThrow()
    {
        var sender = new IdentityNoOpEmailSender<TestUser>();

        var exception = await Record.ExceptionAsync(() =>
            sender.SendPasswordResetLinkAsync(new TestUser(), "test@example.com", "https://example.test/reset"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendPasswordResetCodeAsync_DoesNotThrow()
    {
        var sender = new IdentityNoOpEmailSender<TestUser>();

        var exception = await Record.ExceptionAsync(() =>
            sender.SendPasswordResetCodeAsync(new TestUser(), "test@example.com", "ABCDEF"));

        Assert.Null(exception);
    }
}
