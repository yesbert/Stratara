using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.Core.Models;

/// <summary>Response wrapping the list of claims attached to an authenticated user, as returned by the identity endpoint.</summary>
/// <param name="Claims">The user's claims.</param>
[ExcludeFromCodeCoverage]
public sealed record ClaimsResponse(IEnumerable<ClaimDto> Claims);

/// <summary>A single claim returned from the identity endpoint.</summary>
/// <param name="Type">The claim type (e.g. <c>email</c>, <c>role</c>).</param>
/// <param name="Value">The claim value.</param>
[ExcludeFromCodeCoverage]
public sealed record ClaimDto(string Type, string Value);
