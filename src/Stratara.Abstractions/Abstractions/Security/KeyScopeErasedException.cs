namespace Stratara.Abstractions.Security;

/// <summary>
/// Raised by an <see cref="IKeyStore"/> asked to create or return key material for a scope whose
/// subject has been erased. Nothing is created: data for an erased subject cannot be encrypted again,
/// so a caller answers it as data that is gone.
/// </summary>
public sealed class KeyScopeErasedException : InvalidOperationException
{
    /// <summary>Creates the exception for a refused scope.</summary>
    /// <param name="scope">The scope that was refused.</param>
    public KeyScopeErasedException(KeyScope scope)
        : base(
            $"The key scope {scope.Level} / tenant {scope.TenantId ?? "-"} / user {scope.UserId ?? "-"} names an " +
            "erased subject. No key is created for it, so nothing can be encrypted for the subject again.")
    {
        Scope = scope;
    }

    /// <summary>The scope that was refused.</summary>
    /// <remarks>On an exception that crossed a process boundary this reads as the default scope; the message names it.</remarks>
    public KeyScope Scope { get; }
}
