using Stratara.Contracts.Session;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// A session the way a real one arrives: with a correlation id and a causation id, because the
/// store records both and refuses an entry without a causation.
/// </summary>
public static class PocSessions
{
    public static SessionContext For(Guid tenantId) => new(
        Guid.CreateVersion7().ToString("N"),
        Guid.CreateVersion7().ToString("N"),
        null,
        tenantId,
        tenantId,
        tenantId,
        null);

    public static SessionContext New() => For(Guid.NewGuid());

    /// <summary>A session whose actor is <paramref name="userId"/> in <paramref name="tenantId"/>.</summary>
    public static SessionContext ForUser(Guid tenantId, Guid userId) => new(
        Guid.CreateVersion7().ToString("N"),
        Guid.CreateVersion7().ToString("N"),
        null,
        tenantId,
        userId,
        tenantId,
        userId);
}
