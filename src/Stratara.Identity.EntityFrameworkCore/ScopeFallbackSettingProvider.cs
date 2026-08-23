using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Settings;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Default <see cref="ISettingProvider"/>: walks the scope fallback chain for the current
/// session's Subject — user-in-tenant → user → tenant → global — then the host configuration
/// (<c>Stratara:Settings:&lt;name&gt;</c>), then the definition's code default. Definitions with
/// <c>IsInherited = false</c> consult only the most specific applicable scope before falling
/// through to configuration/default.
/// </summary>
/// <remarks>
/// Register scoped; resolutions are memoized per name within the scope. Without an ambient
/// session only the global scope applies. Reading an undeclared name throws.
/// </remarks>
internal sealed class ScopeFallbackSettingProvider(
    ISettingStore store,
    SettingCatalog catalog,
    ISessionContextProvider? sessionContextProvider = null,
    IConfiguration? configuration = null) : ISettingProvider
{
    private const string ConfigurationSection = "Stratara:Settings";

    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// One read per scope, not one per (setting, scope) pair. A setting resolves by walking its
    /// fallback chain, so twenty inherited settings over a four-scope chain used to be eighty store
    /// round trips; loading each scope once makes it four. The scope's contents are read in full,
    /// which is the trade: more rows per read, far fewer reads.
    /// </summary>
    private readonly Dictionary<SettingScope, IReadOnlyDictionary<string, string>> _scopeCache = [];

    public async Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var definition = catalog.GetOrNull(name)
                         ?? throw new InvalidOperationException(
                             $"Setting '{name}' is not declared in the SettingCatalog; declare it via AddSettingCatalog(...).");

        var value = await ResolveAsync(definition, cancellationToken);
        _cache[name] = value;
        return value;
    }

    public async Task<T> GetAsync<T>(string name, T defaultValue = default!, CancellationToken cancellationToken = default)
    {
        var value = await GetOrNullAsync(name, cancellationToken);
        if (value is null)
        {
            return defaultValue;
        }

        var converter = TypeDescriptor.GetConverter(typeof(T));
        return (T)converter.ConvertFromString(null, CultureInfo.InvariantCulture, value)!;
    }

    private async Task<string?> ResolveAsync(SettingDefinition definition, CancellationToken cancellationToken)
    {
        foreach (var scope in CandidateScopes(definition))
        {
            var values = await ScopeValuesAsync(scope, cancellationToken);
            if (values.TryGetValue(definition.Name, out var value))
            {
                return value;
            }
        }

        return configuration?[$"{ConfigurationSection}:{definition.Name}"] ?? definition.DefaultValue;
    }

    private async Task<IReadOnlyDictionary<string, string>> ScopeValuesAsync(
        SettingScope scope, CancellationToken cancellationToken)
    {
        if (_scopeCache.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        var values = await store.GetAllAsync(scope, cancellationToken);
        _scopeCache[scope] = values;
        return values;
    }

    private IEnumerable<SettingScope> CandidateScopes(SettingDefinition definition)
    {
        var session = sessionContextProvider?.Current;
        var chain = new List<SettingScope>();

        if (session is not null)
        {
            // Through SettingScope's factories rather than formatting the identifiers here. They are
            // the single conversion point from a session's Guids to a scope's string form; a second
            // place doing it by hand is how a scope silently stops matching the rows it wrote.
            if (session.UserId is { } userId)
            {
                chain.Add(SettingScope.ForUserInTenant(session.TenantId, userId));
                chain.Add(SettingScope.ForUser(userId));
            }

            chain.Add(SettingScope.ForTenant(session.TenantId));
        }

        chain.Add(SettingScope.Global);

        return definition.IsInherited ? chain : chain.Take(1);
    }
}
