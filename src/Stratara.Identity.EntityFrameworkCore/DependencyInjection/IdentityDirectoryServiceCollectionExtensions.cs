using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.ApiKeys;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Settings;
using Stratara.Identity.EntityFrameworkCore;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register the Stratara identity-directory plane: the EF-backed tenant
/// membership store, the membership-backed authorization provider, and the membership-backed
/// cross-tenant authorizer.
/// </summary>
public static class IdentityDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Register the EF Core <see cref="ITenantMembershipStore"/> against <typeparamref name="TContext"/>.
    /// The context's model must include the identity-directory tables — derive from
    /// <see cref="IdentityDirectoryDbContext{TContext}"/> or call
    /// <see cref="IdentityDirectoryModelBuilderExtensions.ApplyIdentityDirectoryModel"/> in the
    /// context's <c>OnModelCreating</c>. The context itself must already be registered
    /// (for example via <c>AddDbContext</c> or a pooled factory).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store shares the context registered for the request, so directory operations issued
    /// concurrently within one scope fail on whichever arrives second — a database context serves one
    /// operation at a time — and the store's own commit also commits whatever else the consumer has
    /// left unsaved on that context. Register the store with
    /// <see cref="AddTenantMembershipStoreFromContextFactory{TContext}"/> instead to give each operation its own context.
    /// </para>
    /// <para>
    /// Calling both registrations leaves whichever ran first in place; they do not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// <c>TContext</c> is any context whose model includes the directory tables — derive from
    /// <c>IdentityDirectoryDbContext&lt;TContext&gt;</c> or call <c>ApplyIdentityDirectoryModel()</c> in
    /// <c>OnModelCreating</c>. Every directory store in a request shares this one context:
    /// <code>
    /// services.AddTenantMembershipStore&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddTenantMembershipStore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, SharedDirectoryContextSource<TContext>>();
        services.TryAddScoped<ITenantMembershipStore, EfTenantMembershipStore<TContext>>();
        return services;
    }

    /// <summary>
    /// Register the EF Core <see cref="ITenantMembershipStore"/> against <typeparamref name="TContext"/>,
    /// taking a fresh context for each operation from the registered context factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each operation creates its own context from the registered
    /// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> and disposes it
    /// afterwards, so directory operations issued concurrently within one scope do not contend and a
    /// store's commit reaches only its own rows. In exchange, a store write does not take part in a
    /// transaction the consumer opened on their own scoped context.
    /// </para>
    /// <para>
    /// Requires <c>AddDbContextFactory&lt;TContext&gt;()</c>; a missing registration fails at first
    /// resolution rather than silently. Keep the factory's options aligned with the scoped
    /// registration — interceptors, query filters and conventions are configured per registration,
    /// not per context type.
    /// </para>
    /// <para>
    /// Calling this together with <see cref="AddTenantMembershipStore{TContext}"/> leaves whichever ran first in place; they do
    /// not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Needs <c>AddDbContextFactory&lt;TContext&gt;()</c>. Each operation takes a fresh context, so
    /// directory work issued concurrently in one scope no longer collides — in exchange, a store write
    /// does not join a transaction opened on your own scoped context:
    /// <code>
    /// services.AddDbContextFactory&lt;DirectoryDbContext&gt;();
    /// services.AddTenantMembershipStoreFromContextFactory&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddTenantMembershipStoreFromContextFactory<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, PerOperationDirectoryContextSource<TContext>>();
        services.TryAddScoped<ITenantMembershipStore, EfTenantMembershipStore<TContext>>();
        return services;
    }

    /// <summary>
    /// Register the EF Core <see cref="IApiKeyStore"/> against
    /// <typeparamref name="TContext"/> — issuance (raw key shown once, hash stored), idempotent
    /// import of a caller-supplied key for bootstrap scenarios, fail-closed validation, revocation,
    /// and per-tenant/per-user erasure sweeps. Machine keys are materialized as memberships of
    /// their tenant, so they flow through the same role/permission plane as human actors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store shares the context registered for the request, so directory operations issued
    /// concurrently within one scope fail on whichever arrives second — a database context serves one
    /// operation at a time — and the store's own commit also commits whatever else the consumer has
    /// left unsaved on that context. Register the store with
    /// <see cref="AddApiKeyStoreFromContextFactory{TContext}"/> instead to give each operation its own context.
    /// </para>
    /// <para>
    /// Calling both registrations leaves whichever ran first in place; they do not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddApiKeyStore&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddApiKeyStore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, SharedDirectoryContextSource<TContext>>();
        services.TryAddScoped<IApiKeyStore, EfApiKeyStore<TContext>>();
        return services;
    }

    /// <summary>
    /// Register the EF Core <see cref="IApiKeyStore"/> against <typeparamref name="TContext"/>, taking
    /// a fresh context for each operation from the registered context factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each operation creates its own context from the registered
    /// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> and disposes it
    /// afterwards, so directory operations issued concurrently within one scope do not contend and a
    /// store's commit reaches only its own rows. In exchange, a store write does not take part in a
    /// transaction the consumer opened on their own scoped context.
    /// </para>
    /// <para>
    /// Requires <c>AddDbContextFactory&lt;TContext&gt;()</c>; a missing registration fails at first
    /// resolution rather than silently. Keep the factory's options aligned with the scoped
    /// registration — interceptors, query filters and conventions are configured per registration,
    /// not per context type.
    /// </para>
    /// <para>
    /// Calling this together with <see cref="AddApiKeyStore{TContext}"/> leaves whichever ran first in place; they do
    /// not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Needs <c>AddDbContextFactory&lt;TContext&gt;()</c>; one context per operation:
    /// <code>
    /// services.AddDbContextFactory&lt;DirectoryDbContext&gt;();
    /// services.AddApiKeyStoreFromContextFactory&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddApiKeyStoreFromContextFactory<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, PerOperationDirectoryContextSource<TContext>>();
        services.TryAddScoped<IApiKeyStore, EfApiKeyStore<TContext>>();
        return services;
    }

    /// <summary>
    /// Register <see cref="MembershipAuthorizationProvider"/> as the
    /// <see cref="IAuthorizationProvider"/> — role checks pass on tenant-scoped membership roles
    /// only. Use the generic overload (<see cref="AddMembershipAuthorization{TUser}"/>) when the
    /// host also gates on global ASP.NET Identity roles.
    /// </summary>
    /// <remarks>
    /// Hosts that dispatch through the authorizing mediator pass the provider type there instead
    /// (<c>AddAuthorizingMediator&lt;MembershipAuthorizationProvider&gt;()</c>); this registration
    /// serves components that resolve <see cref="IAuthorizationProvider"/> directly, such as the
    /// authorizing outbox dispatcher.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Resolves roles from tenant-scoped membership. Pair it with the authorizing mediator, which is
    /// what enforces the attributes:
    /// <code>
    /// services.AddMembershipAuthorization();
    /// services.AddAuthorizingMediator&lt;MembershipAuthorizationProvider&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddMembershipAuthorization(this IServiceCollection services)
    {
        services.TryAddScoped<IAuthorizationProvider, MembershipAuthorizationProvider>();
        return services;
    }

    /// <summary>
    /// Register <see cref="MembershipAuthorizationProvider{TUser}"/> as the
    /// <see cref="IAuthorizationProvider"/> — role checks pass on tenant-scoped membership roles
    /// <em>or</em> global ASP.NET Identity roles (platform roles). Requires the host's Identity
    /// registration (<c>UserManager&lt;TUser&gt;</c> resolvable).
    /// </summary>
    /// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// The generic overload adds the user's global ASP.NET Core Identity roles to the membership roles:
    /// <code>
    /// services.AddMembershipAuthorization&lt;ApplicationUser&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddMembershipAuthorization<TUser>(this IServiceCollection services)
        where TUser : class
    {
        services.TryAddScoped<IAuthorizationProvider, MembershipAuthorizationProvider<TUser>>();
        return services;
    }

    /// <summary>
    /// Register <see cref="MembershipCrossTenantAuthorizer"/> as the
    /// <see cref="ICrossTenantAuthorizer"/> consulted by strict tenant isolation: cross-tenant
    /// operations are allowed for actors with an active membership in the data-owner tenant or
    /// with one of the configured cross-tenant roles.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Optional callback to configure the permitted cross-tenant roles.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Strict isolation where only platform administrators may cross tenants without membership:
    /// <code>
    /// builder.Services
    ///     .AddStrataraTenantIsolation(o =&gt; o.Mode = TenantIsolationMode.Strict)
    ///     .AddMembershipCrossTenantAuthorizer(o =&gt; o.CrossTenantRoles.Add("PlatformAdmin"));
    /// </code>
    /// </example>
    public static IServiceCollection AddMembershipCrossTenantAuthorizer(
        this IServiceCollection services,
        Action<MembershipCrossTenantAuthorizerOptions>? configure = null)
    {
        var options = new MembershipCrossTenantAuthorizerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddScoped<ICrossTenantAuthorizer, MembershipCrossTenantAuthorizer>();
        return services;
    }

    /// <summary>
    /// Build and register the application's <see cref="PermissionCatalog"/> (singleton) — the
    /// declared permission vocabulary plus its role→permission grants, consumed by the catalog
    /// permission resolvers and the HTTP permission policies.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Callback that declares permissions and role grants.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// The catalog is the vocabulary: granting a permission it does not declare throws at start-up
    /// rather than silently granting nothing:
    /// <code>
    /// services.AddPermissionCatalog(c =&gt;
    /// {
    ///     c.Add("sims.read", "sims.write");
    ///     c.GrantToRole("TenantAdmin", "sims.read", "sims.write");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddPermissionCatalog(
        this IServiceCollection services,
        Action<PermissionCatalog> configure)
    {
        var catalog = new PermissionCatalog();
        configure(catalog);
        services.AddSingleton(catalog);
        return services;
    }

    /// <summary>
    /// Register <see cref="CatalogPermissionResolver"/> as the <see cref="IPermissionResolver"/>
    /// — permissions derive from tenant-scoped membership roles mapped through the catalog's
    /// role grants. Use the generic overload (<see cref="AddCatalogPermissionResolver{TUser}"/>)
    /// when global ASP.NET Identity roles should also grant permissions.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Resolves a principal's permissions by running its membership roles through the catalog:
    /// <code>
    /// services.AddPermissionCatalog(c =&gt; c.Add("sims.read"));
    /// services.AddCatalogPermissionResolver();
    /// </code>
    /// </example>
    public static IServiceCollection AddCatalogPermissionResolver(this IServiceCollection services)
    {
        services.TryAddScoped<IPermissionResolver, CatalogPermissionResolver>();
        return services;
    }

    /// <summary>
    /// Register <see cref="CatalogPermissionResolver{TUser}"/> as the
    /// <see cref="IPermissionResolver"/> — permissions derive from tenant-scoped membership roles
    /// <em>and</em> global ASP.NET Identity roles, mapped through the catalog's role grants.
    /// Requires the host's Identity registration (<c>UserManager&lt;TUser&gt;</c> resolvable).
    /// </summary>
    /// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// The generic overload also considers the user's global ASP.NET Core Identity roles:
    /// <code>
    /// services.AddCatalogPermissionResolver&lt;ApplicationUser&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddCatalogPermissionResolver<TUser>(this IServiceCollection services)
        where TUser : class
    {
        services.TryAddScoped<IPermissionResolver, CatalogPermissionResolver<TUser>>();
        return services;
    }

    /// <summary>
    /// Build and register the application's <see cref="SettingCatalog"/>
    /// (singleton) — the declared setting vocabulary with defaults and flags, consumed by the
    /// setting provider and the encrypting store decorator.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">Callback that declares the settings.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Declares each setting with its default, whether it inherits down the scope chain, and whether it
    /// is stored encrypted:
    /// <code>
    /// services.AddSettingCatalog(c =&gt; c.Add(
    ///     new SettingDefinition("ui.theme", DefaultValue: "light"),
    ///     new SettingDefinition("billing.vatId", IsInherited: false)));
    /// </code>
    /// </example>
    public static IServiceCollection AddSettingCatalog(
        this IServiceCollection services,
        Action<SettingCatalog> configure)
    {
        var catalog = new SettingCatalog();
        configure(catalog);
        services.AddSingleton(catalog);
        return services;
    }

    /// <summary>
    /// Register the EF Core <see cref="ISettingStore"/> against
    /// <typeparamref name="TContext"/>, plus the scope-fallback
    /// <see cref="ISettingProvider"/> read facade. When the
    /// registered <see cref="SettingCatalog"/> declares encrypted
    /// settings, the store is wrapped in a transparent AES-GCM decorator (requires the security
    /// plane's <see cref="ISecureBlobEncryptor"/> registration —
    /// missing it fails at first resolution, not silently).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store shares the context registered for the request, so directory operations issued
    /// concurrently within one scope fail on whichever arrives second — a database context serves one
    /// operation at a time — and the store's own commit also commits whatever else the consumer has
    /// left unsaved on that context. Register the store with
    /// <see cref="AddSettingStoreFromContextFactory{TContext}"/> instead to give each operation its own
    /// context.
    /// </para>
    /// <para>
    /// Calling both registrations leaves whichever ran first in place; they do not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Registers the store and the provider facade that walks the fallback chain. Declare the
    /// vocabulary first — a setting the catalog does not know has no default to fall back to:
    /// <code>
    /// services.AddSettingCatalog(c =&gt; c.Add(new SettingDefinition("ui.theme", DefaultValue: "light")));
    /// services.AddSettingStore&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddSettingStore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, SharedDirectoryContextSource<TContext>>();
        return services.AddSettingStoreCore<TContext>();
    }

    /// <summary>
    /// Register the EF Core <see cref="ISettingStore"/> against <typeparamref name="TContext"/> and the
    /// scope-fallback <see cref="ISettingProvider"/> read facade, taking a fresh context for each
    /// operation from the registered context factory. The encrypting decorator is applied exactly as
    /// it is for the shared registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each operation creates its own context from the registered
    /// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> and disposes it
    /// afterwards, so directory operations issued concurrently within one scope do not contend and a
    /// store's commit reaches only its own rows. In exchange, a store write does not take part in a
    /// transaction the consumer opened on their own scoped context.
    /// </para>
    /// <para>
    /// Requires <c>AddDbContextFactory&lt;TContext&gt;()</c>; a missing registration fails at first
    /// resolution rather than silently. Keep the factory's options aligned with the scoped
    /// registration — interceptors, query filters and conventions are configured per registration,
    /// not per context type.
    /// </para>
    /// <para>
    /// Calling this together with <see cref="AddSettingStore{TContext}"/> leaves whichever ran first in
    /// place; they do not compose.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContext">The DbContext hosting the directory tables.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Needs <c>AddDbContextFactory&lt;TContext&gt;()</c>; one context per operation:
    /// <code>
    /// services.AddDbContextFactory&lt;DirectoryDbContext&gt;();
    /// services.AddSettingStoreFromContextFactory&lt;DirectoryDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddSettingStoreFromContextFactory<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IDirectoryContextSource<TContext>, PerOperationDirectoryContextSource<TContext>>();
        return services.AddSettingStoreCore<TContext>();
    }

    private static IServiceCollection AddSettingStoreCore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<ISettingStore>(provider =>
        {
            var efStore = new EfSettingStore<TContext>(
                provider.GetRequiredService<IDirectoryContextSource<TContext>>());
            var catalog = provider.GetRequiredService<SettingCatalog>();

            if (!catalog.All.Any(definition => definition.IsEncrypted))
            {
                return efStore;
            }

            var encryptor = provider.GetService<ISecureBlobEncryptor>()
                            ?? throw new InvalidOperationException(
                                "The SettingCatalog declares encrypted settings but no ISecureBlobEncryptor is " +
                                "registered. Call AddStrataraBlobEncryption() (Stratara.Security) or remove the " +
                                "IsEncrypted flags.");
            return new EncryptingSettingStore(efStore, catalog, encryptor);
        });

        services.TryAddScoped<ISettingProvider>(provider =>
            new ScopeFallbackSettingProvider(
                provider.GetRequiredService<ISettingStore>(),
                provider.GetRequiredService<SettingCatalog>(),
                provider.GetService<ISessionContextProvider>(),
                provider.GetService<IConfiguration>()));

        return services;
    }
}
