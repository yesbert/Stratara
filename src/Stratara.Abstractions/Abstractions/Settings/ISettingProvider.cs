namespace Stratara.Abstractions.Settings;

/// <summary>
/// Read facade of the settings plane: resolves a setting for the <em>current session</em> by
/// walking the scope fallback chain — user-in-tenant → user → tenant → global — then the host
/// configuration (<c>Stratara:Settings:&lt;name&gt;</c>), then the definition's code default.
/// Scopes are fed from the session's <em>Subject</em> (data-owner) ids, never the actor.
/// </summary>
/// <remarks>
/// Definitions declared with <c>IsInherited = false</c> consult only the most specific scope
/// applicable to the session before falling through to configuration/default. Reading an
/// undeclared name throws — the catalog is the vocabulary. Writes go through
/// <see cref="ISettingStore"/> directly.
/// </remarks>
public interface ISettingProvider
{
    /// <summary>
    /// Resolves the setting's effective string value for the current session, or <c>null</c>
    /// when no scope, configuration entry, or default carries one.
    /// </summary>
    /// <param name="name">The declared setting name.</param>
    /// <param name="cancellationToken">Token to observe while resolving.</param>
    /// <returns>The effective value, or <c>null</c>.</returns>
    /// <exception cref="InvalidOperationException">The name is not declared in the catalog.</exception>
    Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the setting's effective value converted to <typeparamref name="T"/>, falling
    /// back to <paramref name="defaultValue"/> when unresolved.
    /// </summary>
    /// <typeparam name="T">The target type (convertible from the stored string).</typeparam>
    /// <param name="name">The declared setting name.</param>
    /// <param name="defaultValue">The value to return when nothing resolves.</param>
    /// <param name="cancellationToken">Token to observe while resolving.</param>
    /// <returns>The converted effective value, or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="InvalidOperationException">The name is not declared in the catalog.</exception>
    Task<T> GetAsync<T>(string name, T defaultValue = default!, CancellationToken cancellationToken = default);
}
