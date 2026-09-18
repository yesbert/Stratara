using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// Reads <see cref="ProjectionReplayOptions"/> from the <c>ProjectionReplay</c> section of the
/// configuration the container holds, binds nothing where it holds none, and refuses a lease of zero or
/// less when the host starts. Registered once, at the position of the first
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

    public ValidateOptionsResult Validate(string? name, ProjectionReplayOptions options) =>
        options.LeaseSeconds > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ProjectionReplayOptions.SectionName}:{nameof(ProjectionReplayOptions.LeaseSeconds)} is {options.LeaseSeconds} and must be greater than zero. "
                + "A lease of zero or less lets the replay's marking lapse at once, and publication resumes in the middle of the rebuild.");
}
