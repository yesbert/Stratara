using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Identity.Core.Abstractions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Host-builder extensions that wire up the ASP.NET Core Identity stack with Stratara conventions
/// (password policy, lockout, optional passkey domain) and the channel-agnostic
/// <see cref="IStrataraSignInManager"/> implementation. Channel-specific
/// <see cref="IStrataraAuthenticationStateProvider"/> wiring (Blazor Server, MAUI, etc.) is the
/// consumer's responsibility — Stratara only ships the ASP.NET Core-generic <c>SignInManager</c> wrapper.
/// </summary>
public static class AspCoreIdentityHostBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Registers <c>AddIdentityCore&lt;TUser&gt;</c> with Stratara defaults (length 8, digit + lower + upper + non-alphanumeric,
        /// confirmed-account required, schema v3, optional passkey server-domain from
        /// <c>Identity:Passkey:ServerDomain</c>) plus role + EF stores + default token providers. Does NOT register
        /// the <see cref="IStrataraSignInManager"/> wrapper — use
        /// <see cref="AddAspNetIdentityWithSignInManager{TUser, TIdentityDbContext}"/> for that.
        /// </summary>
        /// <typeparam name="TUser">The Identity user type.</typeparam>
        /// <typeparam name="TIdentityDbContext">The Identity <see cref="DbContext"/> type backing the stores.</typeparam>
        public IHostApplicationBuilder AddAspNetIdentity<TUser, TIdentityDbContext>()
            where TUser : class, new() where TIdentityDbContext : DbContext
        {
            builder.Services.AddIdentityCore<TUser>(ApplyStrataraIdentityDefaults)
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<TIdentityDbContext>()
                .AddDefaultTokenProviders();

            ConfigurePasskeyDomain(builder);

            return builder;
        }

        /// <summary>
        /// Registers the full channel-agnostic ASP.NET Core identity surface:
        /// <see cref="IStrataraSignInManager"/> → <c>AspNetSignInManager&lt;TUser&gt;</c>,
        /// <see cref="IStringLocalizer{T}"/> wiring for i18n'd failure messages,
        /// plus <c>AddIdentityCore&lt;TUser&gt;</c> with Stratara password + lockout + schema v3 defaults,
        /// role + EF stores + sign-in manager + default token providers.
        /// </summary>
        /// <remarks>
        /// Channel-specific <see cref="IStrataraAuthenticationStateProvider"/> wiring (e.g. a Blazor Server
        /// <c>RevalidatingServerAuthenticationStateProvider</c>, MAUI session-state provider) is the consumer's
        /// responsibility — Stratara intentionally stops at the ASP.NET-Core-generic surface to stay
        /// application-agnostic.
        /// </remarks>
        /// <typeparam name="TUser">The Identity user type.</typeparam>
        /// <typeparam name="TIdentityDbContext">The Identity <see cref="DbContext"/> type backing the stores.</typeparam>
        public IHostApplicationBuilder AddAspNetIdentityWithSignInManager<TUser, TIdentityDbContext>()
            where TUser : class, new() where TIdentityDbContext : DbContext
        {
            builder.Services.TryAddScoped<TIdentityDbContext>(sp =>
                sp.GetRequiredService<IDbContextFactory<TIdentityDbContext>>().CreateDbContext());

            builder.Services.AddLocalization();

            builder.Services.AddScoped<IStrataraSignInManager, AspNetSignInManager<TUser>>();

            builder.Services.AddIdentityCore<TUser>(options =>
                {
                    ApplyStrataraIdentityDefaults(options);
                    ApplyStrataraLockoutDefaults(options);
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<TIdentityDbContext>()
                .AddSignInManager()
                .AddDefaultTokenProviders();

            ConfigurePasskeyDomain(builder);

            return builder;
        }

        /// <summary>
        /// Registers <see cref="IdentityNoOpEmailSender{TUser}"/> as the host's <c>IEmailSender&lt;TUser&gt;</c>
        /// for **development environments only**. Throws <see cref="InvalidOperationException"/> when the
        /// host environment is Production to prevent silently dropping confirmation / reset emails in prod.
        /// </summary>
        /// <typeparam name="TUser">The Identity user type.</typeparam>
        /// <exception cref="InvalidOperationException">Thrown when the host environment is Production.</exception>
        public IHostApplicationBuilder AddDevelopmentNoOpEmailSender<TUser>()
            where TUser : class, new()
        {
            if (builder.Environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "IdentityNoOpEmailSender<TUser> must not be used in production environments. " +
                    "Register a real IEmailSender<TUser> implementation (e.g., SendGrid, Mailgun, AWS SES) instead.");
            }

            builder.Services.AddScoped<IEmailSender<TUser>, IdentityNoOpEmailSender<TUser>>();
            return builder;
        }
    }

    private static void ApplyStrataraIdentityDefaults(IdentityOptions options)
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;

        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 1;
    }

    private static void ApplyStrataraLockoutDefaults(IdentityOptions options)
    {
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    }

    private static void ConfigurePasskeyDomain(IHostApplicationBuilder builder)
    {
        var passkeyDomain = builder.Configuration.GetValue<string>("Identity:Passkey:ServerDomain");
        if (!string.IsNullOrEmpty(passkeyDomain))
        {
            builder.Services.Configure<IdentityPasskeyOptions>(options =>
            {
                options.ServerDomain = passkeyDomain;
            });
        }
    }
}
