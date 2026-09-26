using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Erasure;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Settings;

namespace Stratara.Infrastructure.Security;

/// <summary>
/// Composes the framework's four erasure sweeps into one operation. See <see cref="ISubjectEraser"/>
/// for what is covered and what deliberately is not.
/// </summary>
public sealed class SubjectEraser : ISubjectEraser
{
    /// <summary>
    /// The levels whose keys are named after a subject. A key is named by level, tenant and user
    /// together — a tenant-level value written for a user has a key naming both, and a user-level
    /// value written with no user has one naming only the tenant — so a subject's key material is
    /// every scope that names it, at either level, with or without the other dimension.
    /// </summary>
    private static readonly DataSensitivityLevel[] IsolatingLevels =
        [DataSensitivityLevel.UserScoped, DataSensitivityLevel.TenantScoped];

    private readonly ITenantMembershipStore memberships;
    private readonly IApiKeyStore apiKeys;
    private readonly ISettingStore settings;
    private readonly IKeyStore keys;

    /// <summary>Creates the eraser over the four stores it sweeps.</summary>
    /// <param name="memberships">The directory holding memberships and active-tenant selections.</param>
    /// <param name="apiKeys">The API-key store.</param>
    /// <param name="settings">The scoped-setting store.</param>
    /// <param name="keys">The key store whose scopes are shredded last.</param>
    public SubjectEraser(
        ITenantMembershipStore memberships,
        IApiKeyStore apiKeys,
        ISettingStore settings,
        IKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(apiKeys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);

        this.memberships = memberships;
        this.apiKeys = apiKeys;
        this.settings = settings;
        this.keys = keys;
    }

    /// <inheritdoc/>
    public async Task<ErasureReport> EraseUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
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

        await SweepAsync(swept, ErasurePlane.Memberships, [Describe(userId: userId)],
            () => memberships.RemoveAllMembershipsAsync(userId, cancellationToken));

        var keyScopes = IsolatingLevels
            .SelectMany(level => tenantIds
                .Select(t => new KeyScope(level, Format(t), Format(userId)))
                .Prepend(new KeyScope(level, null, Format(userId))))
            .ToList();

        await SweepAsync(swept, ErasurePlane.KeyMaterial, keyScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in keyScopes)
                {
                    await keys.EraseScopeAsync(scope, cancellationToken);
                }
            });

        return new ErasureReport(swept);
    }

    /// <inheritdoc/>
    public async Task<ErasureReport> EraseTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
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

        await SweepAsync(swept, ErasurePlane.Memberships, [Describe(tenantId: tenantId)],
            () => memberships.RemoveAllMembersAsync(tenantId, cancellationToken));

        var keyScopes = IsolatingLevels
            .SelectMany(level => userIds
                .Select(u => new KeyScope(level, Format(tenantId), Format(u)))
                .Prepend(new KeyScope(level, Format(tenantId))))
            .ToList();

        await SweepAsync(swept, ErasurePlane.KeyMaterial, keyScopes.Select(Describe).ToList(),
            async () =>
            {
                foreach (var scope in keyScopes)
                {
                    await keys.EraseScopeAsync(scope, cancellationToken);
                }
            });

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
