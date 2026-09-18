using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Session;

namespace Stratara.Sessions.Session;

/// <summary>
/// Reads <see cref="SessionContextOptions"/> from the <c>SessionContext</c> section of the
/// configuration the container holds, and binds nothing where it holds none. Registered once, at the
/// position of the first <c>AddSessionContext()</c> call, so a later call cannot re-apply the section
/// over a value the host configured in code in between.
/// </summary>
internal sealed class SessionContextOptionsBinding(IServiceProvider services) : IConfigureOptions<SessionContextOptions>
{
    public void Configure(SessionContextOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(SessionContextOptions.SectionName).Bind(options);
}
