namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// The result category of a JIT external-login provisioning attempt. The three success outcomes
/// (<see cref="SignedInExisting"/>, <see cref="Linked"/>, <see cref="Provisioned"/>) yield a local
/// account to sign in; the two others require the caller to stop and prompt the user.
/// </summary>
public enum ExternalLoginProvisioningOutcome
{
    /// <summary>The external login was already linked to a local account; that account was returned.</summary>
    SignedInExisting,

    /// <summary>The external login was linked to a pre-existing local account after the verified-email checks passed.</summary>
    Linked,

    /// <summary>A new local account was created and the external login linked to it.</summary>
    Provisioned,

    /// <summary>
    /// A local account with the same email exists but auto-linking was refused because the email
    /// was not provably verified on both sides. The caller must obtain interactive proof (for
    /// example, sign the user into the local account first, then link) — never merge silently.
    /// </summary>
    RequiresInteractiveLinking,

    /// <summary>
    /// Provisioning was refused: auto-provisioning is disabled and no account matched, the external
    /// login carried no usable email, or the invitation gate rejected the sign-in.
    /// </summary>
    Denied,
}
