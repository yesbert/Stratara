using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Mediator;

namespace Stratara.Mediator.Authorization;

/// <summary>
/// Hosted service that runs at host start-up and fails fast when the application contains command /
/// query types decorated with <see cref="RequireRoleAttribute"/> but the registered <see cref="IMediator"/>
/// does not also implement <see cref="IAuthorizingMediator"/>. Without this check, <c>AddMediator()</c>
/// consumers who add <c>[RequireRole("...")]</c> annotations would have those annotations silently ignored —
/// the wrong default for a security attribute.
/// </summary>
/// <remarks>
/// <para>
/// The validator resolves the runtime <see cref="IMediator"/> from a fresh scope and inspects its
/// concrete type for the <see cref="IAuthorizingMediator"/> marker. This catches custom mediator
/// decorators that wrap <c>AuthorizingMediator</c> as well as alternative authorizing implementations,
/// while still flagging hosts that registered only the plain <c>Mediator</c> alongside
/// <c>[RequireRole]</c> types.
/// </para>
/// <para>
/// Scans <see cref="AppDomain.CurrentDomain"/> loaded assemblies once per <see cref="StartAsync"/> call.
/// Throws <see cref="InvalidOperationException"/> with remediation hints when role-protected types exist
/// but the mediator is not authorizing. Reflection-load failures on individual assemblies (third-party
/// libs with unresolved references) are swallowed silently — the validator does best-effort scanning
/// over the app's own assemblies which always load successfully at this point in the host lifecycle.
/// </para>
/// </remarks>
internal sealed class AuthorizationStartupValidator(IServiceProvider services) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var mediator = scope.ServiceProvider.GetService<IMediator>();
        if (mediator is null)
        {
            return Task.CompletedTask;
        }

        if (mediator is IAuthorizingMediator)
        {
            return Task.CompletedTask;
        }

        var roleProtected = FindRoleProtectedTypes().FirstOrDefault();
        if (roleProtected is null)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            $"Type '{roleProtected.FullName}' is decorated with [RequireRole] but the registered IMediator " +
            $"('{mediator.GetType().FullName}') does not implement IAuthorizingMediator, so role attributes " +
            "will be ignored at dispatch time. Call services.AddAuthorizingMediator<TAuthorizationProvider>() " +
            "instead of services.AddMediator(), or — if you ship a custom mediator decorator that delegates " +
            "to AuthorizingMediator — have the outermost decorator implement IAuthorizingMediator so this " +
            "validator recognises it.");
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IEnumerable<Type> FindRoleProtectedTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                continue;
            }

            foreach (var type in types.Where(t => t.GetCustomAttributes<RequireRoleAttribute>(inherit: true).Any()))
            {
                yield return type;
            }
        }
    }
}
