using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.Outbox.RabbitMQ;

/// <summary>
/// Reads <see cref="RedisCoordinationOptions"/> from the <c>RedisCoordination</c> section of the configuration the
/// container holds, binds nothing where it holds none, and refuses an unusable product name when the host starts.
/// Registered once, at the position of the first registration that needs it, so a later one cannot re-apply the
/// section over a value the host configured in code in between.
/// </summary>
internal sealed class RedisCoordinationOptionsBinding(IServiceProvider services) :
    IConfigureOptions<RedisCoordinationOptions>,
    IValidateOptions<RedisCoordinationOptions>
{
    public void Configure(RedisCoordinationOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(RedisCoordinationOptions.SectionName).Bind(options);

    public ValidateOptionsResult Validate(string? name, RedisCoordinationOptions options)
    {
        var productName = options.ProductName ?? string.Empty;
        return productName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{RedisCoordinationOptions.SectionName}:{nameof(RedisCoordinationOptions.ProductName)} is '{productName}' and may hold only letters, digits, '.', '_' and '-'. "
                + "Every Redis key and channel the framework writes carries this name, and another character could read as part of the key's layout.");
    }
}
