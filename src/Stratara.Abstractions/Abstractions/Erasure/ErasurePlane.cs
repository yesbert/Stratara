namespace Stratara.Abstractions.Erasure;

/// <summary>
/// A plane of storage a subject's data lives in, swept as part of a composed erasure.
/// The sweeps run in the order an <see cref="ErasureReport"/> lists them: API keys, settings, key
/// material, memberships.
/// </summary>
public enum ErasurePlane
{
    /// <summary>API keys bound to the subject. Swept first, so nothing can act on the subject's behalf mid-erasure.</summary>
    ApiKeys,

    /// <summary>Scoped settings belonging to the subject, across every tenant it is a member of.</summary>
    Settings,

    /// <summary>
    /// Directory memberships and the active-tenant selections they carry. Swept last, because they
    /// name the tenants a user belongs to and the members a tenant has — which is how the other
    /// planes find their scopes — so an erasure repeated after a failure still finds them.
    /// </summary>
    Memberships,

    /// <summary>
    /// Key material. Swept after every plane whose data it would make unreadable, so a failure there
    /// never leaves rows nobody can identify.
    /// </summary>
    KeyMaterial
}
