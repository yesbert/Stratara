using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Reads <see cref="MessageRetryOptions"/> from the <c>MessageRetry</c> section of the configuration the
/// container holds for a host whose command dispatcher is the execution model's and which registered no bus
/// transport before it, and binds nothing where the container holds no configuration. Registered once, so a
/// repeated dispatcher registration cannot re-apply the section over a value the host configured in code in
/// between.
/// </summary>
internal sealed class MessageRetryOptionsBinding(IServiceProvider services) : IConfigureOptions<MessageRetryOptions>
{
    public void Configure(MessageRetryOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(MessageRetryOptions.SectionName).Bind(options);

    /// <summary>
    /// Registers the binding and the transports' start-up validation, unless a bus transport already did:
    /// both transports validate the section they bind, so a registered validation marks it as read.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.AddOptions<MessageRetryOptions>();
        if (services.Any(d => d.ServiceType == typeof(IValidateOptions<MessageRetryOptions>)))
        {
            return;
        }

        services.AddOptions<MessageRetryOptions>()
            .Validate(MessageRetryOptionsValidation.IsValid, MessageRetryOptionsValidation.Message)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MessageRetryOptions>, MessageRetryOptionsBinding>());
    }
}
