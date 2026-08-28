using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Multitenancy;
using Stratara.Identity.AspNetCore.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that wire the sign-in claims bridge: the <c>stratara:tenant_id</c> claim is
/// resolved from the user's tenant membership and surfaced on the principal, where Stratara's
/// session-context middleware picks it up. Two modes — stamp at issuance
/// (<see cref="AddMembershipTenantClaim{TUser}"/>) or resolve per request
/// (<see cref="AddMembershipTenantClaimsTransformation"/>).
/// </summary>
public static class MembershipClaimsServiceCollectionExtensions
{
    /// <summary>
    /// Decorate the registered <see cref="IUserClaimsPrincipalFactory{TUser}"/> with
    /// <see cref="MembershipClaimsPrincipalFactory{TUser}"/>, stamping the tenant claim into
    /// every issued principal (cookies and ASP.NET Identity bearer tokens). Call it after the
    /// identity registration (<c>AddAspNetIdentity*</c>) and after registering an
    /// <see cref="ITenantMembershipStore"/>. A tenant switch takes effect on the next sign-in
    /// refresh; use <see cref="AddMembershipTenantClaimsTransformation"/> when it must apply
    /// immediately.
    /// </summary>
    /// <remarks>
    /// The bridge requires Guid-parseable identity keys (ASP.NET Identity's default GUID-string
    /// keys) — the same assumption Stratara's session-context middleware makes. Hosts with
    /// non-Guid user keys get no tenant claim stamped (fail-closed, no error), so verify the key
    /// shape before adopting the bridge.
    /// </remarks>
    /// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IUserClaimsPrincipalFactory{TUser}"/> is registered yet.
    /// </exception>
    /// <example>
    /// Stamps the tenant into every principal as it is issued. A tenant switch after sign-in is not
    /// reflected until the principal is re-issued — use the transformation for that:
    /// <code>
    /// services.AddMembershipTenantClaim&lt;ApplicationUser&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddMembershipTenantClaim<TUser>(this IServiceCollection services)
        where TUser : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IUserClaimsPrincipalFactory<TUser>))
                         ?? throw new InvalidOperationException(
                             $"No IUserClaimsPrincipalFactory<{typeof(TUser).Name}> is registered. " +
                             "Call AddAspNetIdentity/AddAspNetIdentityWithSignInManager (or your identity registration) first.");

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IUserClaimsPrincipalFactory<TUser>),
            provider => new MembershipClaimsPrincipalFactory<TUser>(
                (IUserClaimsPrincipalFactory<TUser>)CreateInner(provider, descriptor),
                provider.GetRequiredService<ITenantMembershipStore>()),
            descriptor.Lifetime));

        return services;
    }

    /// <summary>
    /// Register <see cref="MembershipClaimsTransformation"/> as an
    /// <see cref="IClaimsTransformation"/>, resolving the tenant claim from the membership store
    /// on every request. Choose this mode when tenant switches must apply without re-issuing the
    /// sign-in; it composes with the issuance mode (principals already carrying the claim pass
    /// through unchanged).
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Resolves the tenant claim per request, so a tenant switch applies without re-issuing the sign-in:
    /// <code>
    /// services.AddMembershipTenantClaimsTransformation();
    /// </code>
    /// </example>
    public static IServiceCollection AddMembershipTenantClaimsTransformation(this IServiceCollection services)
    {
        services.TryAddScoped<IClaimsTransformation, MembershipClaimsTransformation>();
        return services;
    }

    private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        return ActivatorUtilities.CreateInstance(
            provider,
            descriptor.ImplementationType
            ?? throw new InvalidOperationException("The decorated claims-factory registration carries no implementation."));
    }
}
