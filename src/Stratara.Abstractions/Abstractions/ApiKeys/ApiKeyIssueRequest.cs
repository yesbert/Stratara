using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// Parameters for issuing a new API key.
/// </summary>
/// <param name="TenantId">The tenant to bind the key to.</param>
/// <param name="Name">Display name for administration.</param>
/// <param name="Roles">
/// Tenant-scoped roles for a machine key. Must be empty when <paramref name="UserId"/> is set —
/// a personal access token acts as the bound user and inherits that user's roles instead.
/// </param>
/// <param name="UserId">
/// Bind the key to a user to issue a personal access token; <c>null</c> issues a machine key.
/// </param>
/// <param name="ExpiresAt">Optional expiry.</param>
[ExcludeFromCodeCoverage]
public sealed record ApiKeyIssueRequest(
    Guid TenantId,
    string Name,
    IReadOnlyCollection<string> Roles,
    Guid? UserId = null,
    DateTimeOffset? ExpiresAt = null);
