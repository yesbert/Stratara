using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Reads <see cref="EventSourcingOptions"/> from the <c>EventSourcing</c> section of the configuration the
/// container holds, and binds nothing where it holds none — a host built on a bare service collection has no
/// configuration to read. Registered once, at the position of the first <c>AddEventSourcing()</c> call, so a
/// value the host configures in code after it is not overwritten.
/// </summary>
internal sealed class EventSourcingOptionsBinding(IServiceProvider services) : IConfigureOptions<EventSourcingOptions>
{
    public void Configure(EventSourcingOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(EventSourcingOptions.SectionName).Bind(options);
}
