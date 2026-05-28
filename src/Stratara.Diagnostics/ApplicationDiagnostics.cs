using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Stratara.Diagnostics;

/// <summary>
/// Shared observability primitives — <see cref="ActivitySource"/> + <see cref="Meter"/> +
/// stable tag-name + metric-name constants used across every Stratara package.
/// </summary>
/// <remarks>
/// The source name <c>"Stratara.Application"</c> and meter name <c>"Stratara.Service"</c> are
/// part of the public observability contract — renaming them breaks downstream
/// OTel-collector / Grafana / Tempo queries. Treat these constants as a stable surface.
/// </remarks>
public static class ApplicationDiagnostics
{
    /// <summary>Tag name <c>correlation.id</c> for cross-service request correlation.</summary>
    public const string CorrelationIdTagName = "correlation.id";

    /// <summary>Tag name <c>causation.id</c> for the immediate-cause request id.</summary>
    public const string CausationIdTagName = "causation.id";

    /// <summary>Tag name <c>tenant.id</c> — the data owner (Subject) tenant id.</summary>
    public const string TenantIdTagName = "tenant.id";

    /// <summary>Tag name <c>user.id</c> — the Actor user id (who triggered the operation).</summary>
    public const string UserIdTagName = "user.id";

    /// <summary>Stratara's shared <see cref="ActivitySource"/>.</summary>
    public static class Activity
    {
        /// <summary>The activity source name — <c>"Stratara.Application"</c>.</summary>
        public const string SourceName = "Stratara.Application";

        /// <summary>The shared <see cref="ActivitySource"/> instance.</summary>
        public static readonly ActivitySource Source = new(SourceName);
    }

    /// <summary>Stratara's shared <see cref="Metrics.Meter"/> and instruments.</summary>
    public static class Metrics
    {
        /// <summary>The meter name — <c>"Stratara.Service"</c>.</summary>
        public const string MeterName = "Stratara.Service";

        /// <summary>The shared <see cref="Metrics.Meter"/> instance.</summary>
        public static readonly Meter Meter = new(MeterName, "1.0.0");

        /// <summary>
        /// Counter that tracks optimistic-concurrency conflicts encountered when appending
        /// events to a stream (write store).
        /// </summary>
        public static readonly Counter<long> EventSourceAppendConflicts = Meter.CreateCounter<long>(
            "event_source.append.conflicts",
            unit: "{conflict}",
            description: "Number of optimistic-concurrency conflicts detected when appending events to a stream.");
    }

    /// <summary>Tag name constants used on metric instruments.</summary>
    public static class MetricTags
    {
        /// <summary>Tag name <c>aggregate.type</c>.</summary>
        public const string AggregateType = "aggregate.type";

        /// <summary>Tag name <c>bucket.id</c> — bucket-lock bucket index.</summary>
        public const string BucketId = "bucket.id";
    }
}
