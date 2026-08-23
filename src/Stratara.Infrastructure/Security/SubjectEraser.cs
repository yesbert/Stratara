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

        var keyScopes = new List<KeyScope> { new(DataSensitivityLevel.UserScoped, null, Format(userId)) };
        keyScopes.AddRange(tenantIds.Select(t =>
            new KeyScope(DataSensitivityLevel.UserScoped, Format(t), Format(userId))));

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

        var keyScopes = new List<KeyScope> { new(DataSensitivityLevel.TenantScoped, Format(tenantId)) };
        keyScopes.AddRange(userIds.Select(u =>
            new KeyScope(DataSensitivityLevel.UserScoped, Format(tenantId), Format(u))));

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
