using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// Parameters for importing a machine key whose raw value the caller already holds — the
/// bootstrap counterpart to <see cref="ApiKeyIssueRequest"/>.
/// </summary>
/// <remarks>
/// There is deliberately no <c>UserId</c>: a personal access token acts as its bound user and is
/// issued interactively by that user, so a caller-supplied value has no meaning there. Import is a
/// machine-key path only.
/// </remarks>
/// <param name="RawKey">
/// The key value to store, in the canonical format (see <see cref="ApiKeyFormat"/>). Generate it
/// with <see cref="ApiKeyFormat.CreateRawKey"/> — hand-written values are rejected.
/// </param>
/// <param name="TenantId">The tenant to bind the key to.</param>
/// <param name="Name">Display name for administration.</param>
/// <param name="Roles">Tenant-scoped roles for the key.</param>
/// <param name="ExpiresAt">Optional expiry.</param>
[ExcludeFromCodeCoverage]
public sealed record ApiKeyImportRequest(
    string RawKey,
    Guid TenantId,
    string Name,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset? ExpiresAt = null);
