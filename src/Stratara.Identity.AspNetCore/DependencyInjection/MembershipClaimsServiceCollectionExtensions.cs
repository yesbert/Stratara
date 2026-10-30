using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Multitenancy;
using Stratara.Identity.AspNetCore.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that wire the sign-in claims bridge: the <c>stratara:tenant_id</c> claim is
/// resolved from the user's tenant membership and surfaced on the principal, where Stratara's
/// session-context middleware picks it up. Three modes — stamp at issuance
/// (<see cref="AddMembershipTenantClaim{TUser}"/>), resolve per request
/// (<see cref="AddMembershipTenantClaimsTransformation"/>), or stamp at issuance, keep it across a
/// refresh and check it on every request (<see cref="AddCheckedMembershipTenantClaim{TUser}"/>).
/// </summary>
/// <remarks>
/// <para>
/// When changes take effect. <b>Stamped</b>: a membership or selection change reaches a signed-in
/// user when the sign-in is re-issued — at the latest when the security-stamp validator refreshes it.
/// <b>Live</b>: a selection change applies on the next request; an ended membership is dropped on the
/// next request. <b>Kept and checked</b>: an ended membership is dropped on the next request; a
/// selection changes this sign-in's tenant only when the host re-issues the sign-in.
/// </para>
/// <para>
/// A user has one selection across everything that shares the identity store, so live resolution
/// moves every open session of the user when any of them switches. Where several applications share
/// one identity store, choose the kept-and-checked mode.
/// </para>
/// </remarks>
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
    /// through unchanged). It cannot be combined with <see cref="AddCheckedMembershipTenantClaim{TUser}"/>.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Resolves the tenant claim per request, so a tenant switch applies without re-issuing the sign-in:
    /// <code>
    /// services.AddMembershipTenantClaimsTransformation();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AddCheckedMembershipTenantClaim{TUser}"/> is already registered.
    /// </exception>
    public static IServiceCollection AddMembershipTenantClaimsTransformation(this IServiceCollection services)
    {
        ThrowIfRegistered<CheckedMembershipClaimsTransformation>(services);
        services.TryAddScoped<IClaimsTransformation, MembershipClaimsTransformation>();
        return services;
    }

    /// <summary>
    /// Stamp the tenant claim when the principal is issued, keep it when the security-stamp validator
    /// refreshes a cookie sign-in, and check it on every request: where the user's membership in the
    /// carried tenant is no longer <see cref="MembershipStatus.Active"/>, the request's principal loses
    /// the claim and resolves as one without it. Use it where several applications share one identity
    /// store, so a switch in one does not move another's open sign-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant changes only when the principal is issued: at sign-in, and when the host re-issues the
    /// sign-in — which is how it switches the user to a tenant it has just selected
    /// (<see cref="ITenantMembershipStore.SetActiveTenantAsync"/>, then a sign-in refresh). A host that
    /// re-issues the sign-in for another reason switches the tenant to the selection as well.
    /// </para>
    /// <para>
    /// Every request looks the carried membership up once. Only principals ASP.NET Identity issued are
    /// checked; an API key or a token your application mints keeps the tenant it carries. A bearer-token
    /// refresh re-creates the principal through the claims factory and resolves the tenant from the
    /// selection; only the cookie refresh keeps it.
    /// </para>
    /// <para>
    /// ASP.NET Core runs one <see cref="IClaimsTransformation"/>. A host that registers one of its own after
    /// this call is refused at start, because the membership check would silently stop.
    /// </para>
    /// </remarks>
    /// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IUserClaimsPrincipalFactory{TUser}"/> is registered yet, or
    /// <see cref="AddMembershipTenantClaimsTransformation"/> is already registered.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddCheckedMembershipTenantClaim&lt;IdentityUser&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddCheckedMembershipTenantClaim<TUser>(this IServiceCollection services)
        where TUser : class
    {
        ThrowIfRegistered<MembershipClaimsTransformation>(services);
        if (services.Any(d => d.ImplementationType == typeof(CheckedMembershipClaimsTransformation)))
        {
            return services;
        }

        services.AddMembershipTenantClaim<TUser>();
        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, CheckedMembershipClaimsTransformation>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CheckedMembershipTenantClaimGuard>());
        services.AddOptions<SecurityStampValidatorOptions>()
            .PostConfigure<IHttpContextAccessor>((options, accessor) =>
            {
                var hostHandler = options.OnRefreshingPrincipal;
                options.OnRefreshingPrincipal = async context =>
                {
                    if (hostHandler is not null)
                    {
                        await hostHandler(context);
                    }

                    var httpContext = accessor.HttpContext;
                    await MembershipTenantClaimRefresh.KeepTenantAsync(
                        context,
                        httpContext?.RequestServices.GetService<ITenantMembershipStore>(),
                        httpContext?.RequestAborted ?? CancellationToken.None);
                };
            });

        return services;
    }

    private static void ThrowIfRegistered<TConflicting>(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IClaimsTransformation) && d.ImplementationType == typeof(TConflicting)))
        {
            throw new InvalidOperationException(
                "AddCheckedMembershipTenantClaim and AddMembershipTenantClaimsTransformation decide the tenant claim " +
                "differently and only one claims transformation runs; register one of them.");
        }
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
