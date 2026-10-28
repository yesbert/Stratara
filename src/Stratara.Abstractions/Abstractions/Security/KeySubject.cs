namespace Stratara.Abstractions.Security;

/// <summary>
/// A tenant or a user whose erasure a key store records, so that it creates no key for the subject
/// afterwards. <see cref="Covers"/> decides which scopes that is — the same keys the subject's erasure
/// shreds.
/// </summary>
/// <remarks>
/// Two subjects are equal when their kind matches and their ids match without regard to case, the
/// same comparison <see cref="Covers"/> uses, so a set of subjects holds each one once.
/// </remarks>
public readonly record struct KeySubject
{
    private KeySubject(KeySubjectKind kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Kind = kind;
        _id = id;
    }

    private readonly string? _id;

    /// <summary>Whether the subject is a tenant or a user.</summary>
    public KeySubjectKind Kind { get; }

    /// <summary>The subject's id, as it appears in a <see cref="KeyScope"/>; empty on the default value, which names no subject.</summary>
    public string Id => _id ?? string.Empty;

    /// <summary>Names a tenant.</summary>
    /// <param name="tenantId">The tenant id, as <see cref="KeyScope.TenantId"/> carries it.</param>
    /// <returns>The tenant subject.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="tenantId"/> is <see langword="null"/>.</exception>
    public static KeySubject Tenant(string tenantId) => new(KeySubjectKind.Tenant, tenantId);

    /// <summary>Names a user.</summary>
    /// <param name="userId">The user id, as <see cref="KeyScope.UserId"/> carries it.</param>
    /// <returns>The user subject.</returns>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="userId"/> is <see langword="null"/>.</exception>
    public static KeySubject User(string userId) => new(KeySubjectKind.User, userId);

    /// <summary>
    /// Whether the subject's erasure covers a scope: for a tenant, a scope of any level that names the
    /// tenant; for a user, a <see cref="DataSensitivityLevel.UserScoped"/> scope that names the user.
    /// Ids compare without regard to case, so a GUID written in either case is matched. The default
    /// value names no subject and covers nothing.
    /// </summary>
    /// <param name="scope">The scope to test.</param>
    /// <returns><see langword="true"/> when the scope belongs to the subject's erasure.</returns>
    public bool Covers(KeyScope scope) => Kind switch
    {
        KeySubjectKind.Tenant => Matches(scope.TenantId),
        KeySubjectKind.User => scope.Level == DataSensitivityLevel.UserScoped && Matches(scope.UserId),
        _ => false
    };

    private bool Matches(string? scopeId) =>
        _id is not null && string.Equals(scopeId, _id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws when the value is the default one, which names no subject.</summary>
    /// <param name="paramName">The name of the parameter that carried the subject.</param>
    /// <exception cref="ArgumentException">The subject was not created through <see cref="Tenant"/> or <see cref="User"/>.</exception>
    public void ThrowIfDefault(string paramName)
    {
        if (_id is null)
        {
            throw new ArgumentException("The default KeySubject names no subject; create one with KeySubject.Tenant or KeySubject.User.", paramName);
        }
    }

    /// <summary>Whether both name the same subject: the same kind, and ids equal without regard to case.</summary>
    /// <param name="other">The subject to compare with.</param>
    /// <returns><see langword="true"/> when both name the same subject.</returns>
    public bool Equals(KeySubject other) =>
        Kind == other.Kind && string.Equals(_id, other._id, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Kind, _id is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_id));
}
