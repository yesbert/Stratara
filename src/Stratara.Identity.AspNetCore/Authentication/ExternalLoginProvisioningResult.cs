namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// The outcome of a JIT external-login provisioning attempt, plus the resolved local account when
/// one is available.
/// </summary>
/// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
/// <param name="Outcome">The result category.</param>
/// <param name="User">The resolved local account for the success outcomes and <see cref="ExternalLoginProvisioningOutcome.RequiresInteractiveLinking"/>; <c>null</c> for <see cref="ExternalLoginProvisioningOutcome.Denied"/>.</param>
/// <param name="Reason">A short human-readable reason for a non-success outcome; <c>null</c> on success.</param>
public sealed record ExternalLoginProvisioningResult<TUser>(
    ExternalLoginProvisioningOutcome Outcome,
    TUser? User,
    string? Reason)
    where TUser : class
{
    /// <summary>
    /// Whether the attempt yielded a signed-in-ready account
    /// (<see cref="ExternalLoginProvisioningOutcome.SignedInExisting"/>,
    /// <see cref="ExternalLoginProvisioningOutcome.Linked"/>, or
    /// <see cref="ExternalLoginProvisioningOutcome.Provisioned"/>).
    /// </summary>
    public bool Succeeded => Outcome
        is ExternalLoginProvisioningOutcome.SignedInExisting
        or ExternalLoginProvisioningOutcome.Linked
        or ExternalLoginProvisioningOutcome.Provisioned;
}
