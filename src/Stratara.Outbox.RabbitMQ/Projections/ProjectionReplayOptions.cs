using System.Diagnostics.CodeAnalysis;

namespace Stratara.Outbox.RabbitMQ.Projections;

/// <summary>
/// Options controlling how long a projection replay's active marking and progress counters survive
/// without renewal. <c>AddProjectionReplayState()</c> — which the outbox dispatcher and the worker
/// composites call — reads them from the configuration section <see cref="SectionName"/>
/// (<c>"ProjectionReplay"</c>) of the host's configuration; a service collection that holds no
/// configuration gets the defaults. A value set with
/// <c>services.Configure&lt;ProjectionReplayOptions&gt;(...)</c> after that call takes precedence over
/// the section.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ProjectionReplayOptions
{
    /// <summary>Configuration section name (<c>"ProjectionReplay"</c>) used to bind these options.</summary>
    public const string SectionName = "ProjectionReplay";

    /// <summary>
    /// Seconds the replay's active marking and its progress counters survive without renewal. The
    /// replay renews them every time it reports progress, so this value must outlast the longest
    /// stretch between two reports — the slowest batch, and the read-model truncation that precedes
    /// the first report. A value shorter than that lets the marking lapse while the replay is still
    /// running, which resumes suppressed publication against half-rebuilt read models; erring long
    /// only delays the clearing of a marking whose replay already died. Must be greater than zero; a
    /// value of zero or less is refused when the host starts. Defaults to 300.
    /// </summary>
    public int LeaseSeconds { get; set; } = 300;
}
