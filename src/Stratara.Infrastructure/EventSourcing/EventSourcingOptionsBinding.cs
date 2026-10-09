using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.EventSourcing;

/// <summary>
/// Reads <see cref="EventSourcingOptions"/> from the <c>EventSourcing</c> section of the configuration the
/// container holds, and binds nothing where it holds none — a host built on a bare service collection has no
/// configuration to read, and refuses a <see cref="EventSourcingOptions.NewStreamOwnerFromSession"/> that names no
/// policy when the host starts — an undefined value would otherwise behave as <see cref="NewStreamOwnerPolicy.Allow"/>
/// while the operator believes the guard is on. Registered once, at the position of the first <c>AddEventSourcing()</c>
/// call, so a value the host configures in code after it is not overwritten.
/// </summary>
internal sealed class EventSourcingOptionsBinding(IServiceProvider services) :
    IConfigureOptions<EventSourcingOptions>,
    IValidateOptions<EventSourcingOptions>
{
    public void Configure(EventSourcingOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(EventSourcingOptions.SectionName).Bind(options);

    public ValidateOptionsResult Validate(string? name, EventSourcingOptions options) =>
        Enum.IsDefined(options.NewStreamOwnerFromSession)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{EventSourcingOptions.SectionName}:{nameof(EventSourcingOptions.NewStreamOwnerFromSession)} is " +
                $"{(int)options.NewStreamOwnerFromSession}, which names no policy. Use Allow, Warn or Refuse.");
}
