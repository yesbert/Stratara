using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// Options for the JIT external-login provisioning service. Every default encodes a security
/// invariant, not a convenience — the safe behavior is on out of the box and must be deliberately
/// relaxed.
/// </summary>
/// <remarks>
/// The most important invariant is that an external login is auto-linked to a pre-existing local
/// account only when the email is provably verified on both sides (nOAuth defense against IdPs that
/// expose a mutable, unverified email claim). Relaxing <see cref="RequireVerifiedEmailForLinking"/>
/// re-opens that account-takeover vector; do it only for a provider you fully trust.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class ExternalLoginProvisioningOptions
{
    /// <summary>
    /// Whether auto-linking an external login to an existing local account requires the email to be
    /// verified by the provider <em>and</em> already confirmed locally. Defaults to <c>true</c>.
    /// When it cannot be satisfied, provisioning returns
    /// <see cref="ExternalLoginProvisioningOutcome.RequiresInteractiveLinking"/> instead of merging.
    /// </summary>
    public bool RequireVerifiedEmailForLinking { get; set; } = true;

    /// <summary>
    /// Whether a first-time external sign-in with no matching local account creates one. Defaults to
    /// <c>true</c>. Set to <c>false</c> to require accounts to be pre-created (or invitation-gated);
    /// an unmatched sign-in then returns <see cref="ExternalLoginProvisioningOutcome.Denied"/>.
    /// </summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>
    /// The claim types read to decide whether the provider asserted the email as verified. Defaults
    /// to <c>email_verified</c> (standard OIDC) and <c>xms_edov</c> (Entra "email domain owner
    /// verified"). A claim value of <c>true</c>/<c>1</c> counts as verified.
    /// </summary>
    public IList<string> EmailVerifiedClaimTypes { get; set; } = ["email_verified", "xms_edov"];

    /// <summary>
    /// An optional gate invoked before any account is created or linked. Return <c>false</c> to
    /// reject the sign-in (for example, no pending invitation) — provisioning then returns
    /// <see cref="ExternalLoginProvisioningOutcome.Denied"/>. When <c>null</c>, no gate is applied.
    /// </summary>
    public Func<ExternalLoginProvisioningContext, CancellationToken, Task<bool>>? InvitationGate { get; set; }
}
