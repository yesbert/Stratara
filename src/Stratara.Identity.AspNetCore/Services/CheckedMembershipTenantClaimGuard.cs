using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stratara.Identity.AspNetCore.Services;

/// <summary>
/// Refuses to start a host that registered the kept-and-checked tenant-claim mode and then another
/// <see cref="IClaimsTransformation"/>: ASP.NET Core runs only the last one registered, so the membership
/// check would silently stop while the cookie refresh keeps the tenant.
/// </summary>
internal sealed class CheckedMembershipTenantClaimGuard(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var transformation = scope.ServiceProvider.GetService<IClaimsTransformation>();
        if (transformation is not CheckedMembershipClaimsTransformation)
        {
            throw new InvalidOperationException(
                $"AddCheckedMembershipTenantClaim is registered, but the claims transformation ASP.NET Core runs is " +
                $"{transformation?.GetType().FullName ?? "none"}, so the tenant claim is no longer checked on every request. " +
                "ASP.NET Core runs one claims transformation; let your own call CheckedMembershipClaimsTransformation, " +
                "or drop the checked mode.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
