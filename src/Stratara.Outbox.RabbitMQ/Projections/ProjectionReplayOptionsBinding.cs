using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// Reads <see cref="ProjectionReplayOptions"/> from the <c>ProjectionReplay</c> section of the
/// configuration the container holds, binds nothing where it holds none, and refuses a lease of zero or
/// less, and a refresh period of zero or less or at or above the lease, when the host starts. Registered once, at the position of the first
/// <c>AddProjectionReplayState()</c> call, so a later call — the outbox dispatcher and the worker
/// composites make one each — cannot re-apply the section over a value the host configured in code in
/// between.
/// </summary>
internal sealed class ProjectionReplayOptionsBinding(IServiceProvider services) :
    IConfigureOptions<ProjectionReplayOptions>,
    IValidateOptions<ProjectionReplayOptions>
{
    public void Configure(ProjectionReplayOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(ProjectionReplayOptions.SectionName).Bind(options);

    public ValidateOptionsResult Validate(string? name, ProjectionReplayOptions options)
    {
        if (options.LeaseSeconds <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{ProjectionReplayOptions.SectionName}:{nameof(ProjectionReplayOptions.LeaseSeconds)} is {options.LeaseSeconds} and must be greater than zero. "
                + "A lease of zero or less lets the replay's marking lapse at once, and publication resumes in the middle of the rebuild.");
        }

        if (options.RefreshSeconds <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{ProjectionReplayOptions.SectionName}:{nameof(ProjectionReplayOptions.RefreshSeconds)} is {options.RefreshSeconds} and must be greater than zero. "
                + "The refresh period bounds how long a host answers from a replay marking that changed elsewhere.");
        }

        if (options.RefreshSeconds >= options.LeaseSeconds)
        {
            return ValidateOptionsResult.Fail(
                $"{ProjectionReplayOptions.SectionName}:{nameof(ProjectionReplayOptions.RefreshSeconds)} is {options.RefreshSeconds} and must be below "
                + $"{ProjectionReplayOptions.SectionName}:{nameof(ProjectionReplayOptions.LeaseSeconds)}, which is {options.LeaseSeconds}. "
                + "A refresh slower than the lease could miss a whole replay.");
        }

        return ValidateOptionsResult.Success;
    }
}
