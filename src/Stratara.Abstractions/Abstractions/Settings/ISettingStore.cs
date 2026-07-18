namespace Stratara.Abstractions.Settings;

/// <summary>
/// Persistence SPI of the settings plane: string values addressed by setting name and exact
/// <see cref="SettingScope"/>. Row-per-key storage keeps field-wise fallback, partial updates,
/// SQL querying, and one-call erasure sweeps.
/// </summary>
/// <remarks>
/// The store is deliberately catalog-unaware and untyped — the read facade
/// (<see cref="ISettingProvider"/>) layers definitions, fallback, and typing on top. Write
/// access (admin UIs, user preference endpoints) goes through this store directly.
/// </remarks>
public interface ISettingStore
{
    /// <summary>
    /// Gets the value stored for the name in exactly this scope, or <c>null</c> when none is set.
    /// </summary>
    /// <param name="name">The setting name.</param>
    /// <param name="scope">The exact scope to read.</param>
    /// <param name="cancellationToken">Token to observe while reading.</param>
    /// <returns>The stored value, or <c>null</c>.</returns>
    Task<string?> GetOrNullAsync(string name, SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every value stored in exactly this scope, keyed by setting name.
    /// </summary>
    /// <param name="scope">The exact scope to read.</param>
    /// <param name="cancellationToken">Token to observe while reading.</param>
    /// <returns>The scope's values; empty when the scope holds none.</returns>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the value for the name in exactly this scope; <c>null</c> deletes the entry.
    /// </summary>
    /// <param name="name">The setting name.</param>
    /// <param name="value">The value to store, or <c>null</c> to remove the scope's value.</param>
    /// <param name="scope">The exact scope to write.</param>
    /// <param name="cancellationToken">Token to observe while writing.</param>
    Task SetAsync(string name, string? value, SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erasure sweep — the settings-plane sibling of the key store's scope erasure. Unlike the
    /// exact-scope reads/writes, the sweep widens per dimension: a user scope deletes the user's
    /// values in <em>every</em> tenant (and their user-global values), a tenant scope deletes the
    /// tenant's values for every user (and its tenant-global values), the global scope deletes
    /// only global values.
    /// </summary>
    /// <param name="scope">The scope naming the user and/or tenant to erase.</param>
    /// <param name="cancellationToken">Token to observe while deleting.</param>
    Task DeleteScopeAsync(SettingScope scope, CancellationToken cancellationToken = default);
}
