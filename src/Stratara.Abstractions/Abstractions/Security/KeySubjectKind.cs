namespace Stratara.Abstractions.Security;

/// <summary>The kind of subject a <see cref="KeySubject"/> names.</summary>
public enum KeySubjectKind
{
    /// <summary>A tenant: every key that names it belongs to its erasure, at any level.</summary>
    Tenant,

    /// <summary>A user: every <see cref="DataSensitivityLevel.UserScoped"/> key that names it belongs to its erasure.</summary>
    User
}
