namespace Stratara.Abstractions.Erasure;

/// <summary>
/// A plane of storage a subject's data lives in, swept as part of a composed erasure.
/// The declaration order is the order the sweeps run in.
/// </summary>
public enum ErasurePlane
{
    /// <summary>API keys bound to the subject. Swept first, so nothing can act on the subject's behalf mid-erasure.</summary>
    ApiKeys,

    /// <summary>Scoped settings belonging to the subject, across every tenant it is a member of.</summary>
    Settings,

    /// <summary>Directory memberships and the active-tenant selections they carry.</summary>
    Memberships,

    /// <summary>Key material. Swept last, because shredding it makes every earlier plane unreadable.</summary>
    KeyMaterial
}
