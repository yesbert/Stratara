using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;

/// <summary>
/// Reads <see cref="ReadModelRestoreOptions"/> from the <c>ProjectionReplay:Restore</c> section of the configuration the
/// container holds, binds nothing where it holds none, and refuses an empty schema name or a command timeout of zero or
/// less when the host starts. Registered once, at the position of the first <c>AddReadModelRestore</c> call.
/// </summary>
internal sealed class ReadModelRestoreOptionsBinding(IServiceProvider services) :
    IConfigureOptions<ReadModelRestoreOptions>,
    IValidateOptions<ReadModelRestoreOptions>
{
    public void Configure(ReadModelRestoreOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(ReadModelRestoreOptions.SectionName).Bind(options);

    public ValidateOptionsResult Validate(string? name, ReadModelRestoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            return ValidateOptionsResult.Fail(
                $"{ReadModelRestoreOptions.SectionName}:{nameof(ReadModelRestoreOptions.Schema)} is empty. Name the schema the " +
                "preserved read models are kept in.");
        }

        if (options.CommandTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{ReadModelRestoreOptions.SectionName}:{nameof(ReadModelRestoreOptions.CommandTimeout)} is {options.CommandTimeout} and must be " +
                "greater than zero.");
        }

        return ValidateOptionsResult.Success;
    }
}
