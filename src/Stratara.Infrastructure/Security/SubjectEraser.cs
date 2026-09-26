using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Erasure;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;
using Stratara.Diagnostics;

namespace Stratara.Infrastructure.Security;

/// <summary>
/// Composes the framework's four erasure sweeps into one operation. See <see cref="ISubjectEraser"/>
/// for what is covered and what deliberately is not.
/// </summary>
public sealed partial class SubjectEraser : ISubjectEraser
{
    /// <summary>
    /// A key is named by level, tenant and user together, whatever the level: a tenant-level value
    /// written for a user has a key naming both, a user-level value written with no user has one
    /// naming only the tenant. The level decides whose erasure a value dies with. Every level is
    /// the tenant's, so a tenant's erasure shreds each of them wherever it names the tenant; only
    /// the user level is the user's.
    /// </summary>
    private static readonly DataSensitivityLevel[] TenantLevels =
        [DataSensitivityLevel.UserScoped, DataSensitivityLevel.TenantScoped, DataSensitivityLevel.Confidential];

    private static readonly DataSensitivityLevel[] UserLevels = [DataSensitivityLevel.UserScoped];

    private readonly ITenantMembershipStore memberships;
    private readonly IApiKeyStore apiKeys;
    private readonly ISettingStore settings;
    private readonly IKeyStore keys;
    private readonly ILogger<SubjectEraser> logger;

    /// <summary>Creates the eraser over the four stores it sweeps.</summary>
    /// <param name="memberships">The directory holding memberships and active-tenant selections.</param>
    /// <param name="apiKeys">The API-key store.</param>
    /// <param name="settings">The scoped-setting store.</param>
    /// <param name="keys">The key store whose scopes are shredded.</param>
    public SubjectEraser(
        ITenantMembershipStore memberships,
        IApiKeyStore apiKeys,
        ISettingStore settings,
        IKeyStore keys)
        : this(memberships, apiKeys, settings, keys, NullLogger<SubjectEraser>.Instance)
    {
    }

    /// <summary>Creates the eraser over the four stores it sweeps, logging what it cannot reach.</summary>
    /// <param name="memberships">The directory holding memberships and active-tenant selections.</param>
    /// <param name="apiKeys">The API-key store.</param>
    /// <param name="settings">The scoped-setting store.</param>
    /// <param name="keys">The key store whose scopes are shredded.</param>
    /// <param name="logger">Warns when the key store cannot list its scopes.</param>
    public SubjectEraser(
        ITenantMembershipStore memberships,
        IApiKeyStore apiKeys,
        ISettingStore settings,
        IKeyStore keys,
        ILogger<SubjectEraser> logger)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(apiKeys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);

        this.memberships = memberships;
        this.apiKeys = apiKeys;
        this.settings = settings;
        this.keys = keys;
        this.logger = logger;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is empty.</exception>
    public async Task<ErasureReport> EraseUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        RejectEmpty(userId, nameof(userId));

        var tenantIds = (await memberships.GetMembershipsAsync(userId, cancellationToken))
            .Select(m => m.TenantId)
            .Distinct()
            .ToList();

        var swept = new List<ErasedPlane>();

        await SweepAsync(swept, ErasurePlane.ApiKeys, [Describe(userId: userId)],
            () => apiKeys.RemoveAllForUserAsync(userId, cancellationToken));

        var settingScopes = new List<SettingScope> { SettingScope.ForUser(userId) };
        settingScopes.AddRange(tenantIds.Select(t => SettingScope.ForUserInTenant(t, userId)));

        await SweepAsync(swept, ErasurePlane.Settings, settingScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in settingScopes)
                {
                    await settings.DeleteScopeAsync(scope, cancellationToken);
                }
            });

        var directoryScopes = UserLevels
            .SelectMany(level => tenantIds
                .Select(t => new KeyScope(level, Format(t), Format(userId)))
                .Prepend(new KeyScope(level, null, Format(userId))));
        var keyScopes = await WithListedScopesAsync(swept, directoryScopes,
            scope => UserLevels.Contains(scope.Level) && Names(scope.UserId, userId), Describe(userId: userId), cancellationToken);

        await SweepAsync(swept, ErasurePlane.KeyMaterial, keyScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in keyScopes)
                {
                    await keys.EraseScopeAsync(scope, cancellationToken);
                }
            });

        await SweepAsync(swept, ErasurePlane.Memberships, [Describe(userId: userId)],
            () => memberships.RemoveAllMembershipsAsync(userId, cancellationToken));

        return new ErasureReport(swept);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    public async Task<ErasureReport> EraseTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        RejectEmpty(tenantId, nameof(tenantId));

        var userIds = (await memberships.GetMembersAsync(tenantId, cancellationToken))
            .Select(m => m.UserId)
            .Distinct()
            .ToList();

        var swept = new List<ErasedPlane>();

        await SweepAsync(swept, ErasurePlane.ApiKeys, [Describe(tenantId: tenantId)],
            () => apiKeys.RemoveAllForTenantAsync(tenantId, cancellationToken));

        var settingScopes = new List<SettingScope> { SettingScope.ForTenant(tenantId) };
        settingScopes.AddRange(userIds.Select(u => SettingScope.ForUserInTenant(tenantId, u)));

        await SweepAsync(swept, ErasurePlane.Settings, settingScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in settingScopes)
                {
                    await settings.DeleteScopeAsync(scope, cancellationToken);
                }
            });

        var directoryScopes = TenantLevels
            .SelectMany(level => userIds
                .Select(u => new KeyScope(level, Format(tenantId), Format(u)))
                .Prepend(new KeyScope(level, Format(tenantId))));
        var keyScopes = await WithListedScopesAsync(swept, directoryScopes,
            scope => TenantLevels.Contains(scope.Level) && Names(scope.TenantId, tenantId), Describe(tenantId: tenantId), cancellationToken);

        await SweepAsync(swept, ErasurePlane.KeyMaterial, keyScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in keyScopes)
                {
                    await keys.EraseScopeAsync(scope, cancellationToken);
                }
            });

        await SweepAsync(swept, ErasurePlane.Memberships, [Describe(tenantId: tenantId)],
            () => memberships.RemoveAllMembersAsync(tenantId, cancellationToken));

        return new ErasureReport(swept);
    }

    private static async Task SweepAsync(
        List<ErasedPlane> swept,
        ErasurePlane plane,
        IReadOnlyList<string> scopes,
        Func<Task> sweep)
    {
        try
        {
            await sweep();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ErasureIncompleteException(plane, new ErasureReport(swept), ex);
        }

        swept.Add(new ErasedPlane(plane, scopes));
    }

    /// <summary>
    /// The scopes the directory names, together with every scope the key store lists that names the
    /// subject — which reaches a key the subject shares with someone no longer in the directory. A
    /// store that cannot list its scopes leaves the directory's alone.
    /// </summary>
    private async Task<List<KeyScope>> WithListedScopesAsync(
        List<ErasedPlane> swept,
        IEnumerable<KeyScope> directoryScopes,
        Func<KeyScope, bool> namesSubject,
        string subject,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyScope> listed;
        try
        {
            listed = await keys.ListScopesAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            LogKeyScopesNotListable(logger, keys.GetType().Name, subject);
            listed = [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ErasureIncompleteException(ErasurePlane.KeyMaterial, new ErasureReport(swept), ex);
        }

        return directoryScopes.Concat(listed.Where(namesSubject)).Distinct().ToList();
    }

    /// <summary>
    /// An empty id is not a subject: the system actor and data with no tenant are keyed by it, so
    /// erasing it would shred every tenant's anonymous or system-wide data.
    /// </summary>
    private static void RejectEmpty(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An empty id names no subject to erase.", parameterName);
        }
    }

    [LoggerMessage(
        EventId = LogEvents.KeyManagement.KeyScopesNotListable,
        Level = LogLevel.Warning,
        Message = "{KeyStore} cannot list its key scopes, so the erasure of {Subject} shreds only the keys the directory names; a key shared with someone no longer in the directory is left readable. Implement IKeyStore.ListScopesAsync to close the gap.")]
    private static partial void LogKeyScopesNotListable(ILogger logger, string keyStore, string subject);

    private static bool Names(string? scopeId, Guid id) =>
        string.Equals(scopeId, Format(id), StringComparison.OrdinalIgnoreCase);

    private static string Format(Guid id) => id.ToString("D");

    private static string Describe(Guid? tenantId = null, Guid? userId = null) => (tenantId, userId) switch
    {
        ({ } t, { } u) => $"tenant {Format(t)} / user {Format(u)}",
        ({ } t, null) => $"tenant {Format(t)}",
        (null, { } u) => $"user {Format(u)}",
        _ => "global"
    };

    private static string Describe(SettingScope scope) =>
        $"setting scope {scope.TenantId ?? "-"} / {scope.UserId ?? "-"}";

    private static string Describe(KeyScope scope) =>
        $"key scope {scope.Level} {scope.TenantId ?? "-"} / {scope.UserId ?? "-"}";
}
