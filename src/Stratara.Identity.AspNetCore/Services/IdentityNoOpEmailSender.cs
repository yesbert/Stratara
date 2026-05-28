using Microsoft.AspNetCore.Identity;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Development-time <see cref="IEmailSender{TUser}"/> that silently drops every email
/// (returns <see cref="Task.CompletedTask"/> without sending anything).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never register this in production.</strong> Use
/// <c>AddDevelopmentNoOpEmailSender&lt;TUser&gt;</c> on the host builder
/// to wire it via DI — that extension throws when the host environment is Production.
/// </para>
/// <para>
/// In production register a real <see cref="IEmailSender{TUser}"/> implementation (e.g. SendGrid,
/// Mailgun, AWS SES) instead.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The ASP.NET Core Identity user type.</typeparam>
public sealed class IdentityNoOpEmailSender<TUser> : IEmailSender<TUser> where TUser : class, new()
{
    /// <summary>Drops a confirmation-link email.</summary>
    /// <param name="user">The identity user.</param>
    /// <param name="email">Recipient email address.</param>
    /// <param name="confirmationLink">Absolute URL that confirms the account when visited.</param>
    public Task SendConfirmationLinkAsync(TUser user, string email, string confirmationLink) => Task.CompletedTask;

    /// <summary>Drops a password-reset-link email.</summary>
    /// <param name="user">The identity user.</param>
    /// <param name="email">Recipient email address.</param>
    /// <param name="resetLink">Absolute URL that opens the password-reset flow when visited.</param>
    public Task SendPasswordResetLinkAsync(TUser user, string email, string resetLink) => Task.CompletedTask;

    /// <summary>Drops a password-reset-code email.</summary>
    /// <param name="user">The identity user.</param>
    /// <param name="email">Recipient email address.</param>
    /// <param name="resetCode">The reset code to display to the user.</param>
    public Task SendPasswordResetCodeAsync(TUser user, string email, string resetCode) => Task.CompletedTask;
}
