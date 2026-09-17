namespace Stratara.Testing.Orleans;

/// <summary>
/// The periods <see cref="ExecutionModelTestHost"/> runs the execution model with — each shortened from the
/// framework's default so that a projection applies and a timer fires within seconds. A value a test sets through a
/// registration's own settings, such as <c>AddStrataraProjectionGrains(o =&gt; o.PollInterval = ...)</c>, wins over the
/// host's; the host applies these only where a setting still holds the framework's default.
/// </summary>
public sealed class ExecutionModelTestHostOptions
{
    /// <summary>
    /// The runtime's minimum reminder period, which every keep-alive and retry period must reach. Defaults to one second;
    /// the host lowers the runtime's minimum to it, so a period of one second starts.
    /// </summary>
    public TimeSpan MinimumReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often a store reader polls the store when nothing wakes it. Defaults to 250 milliseconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The keep-alive period of the store readers and the singleton work, and the retry period of the durable timers.
    /// Defaults to one second.
    /// </summary>
    public TimeSpan ReminderPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a recorded command waits for its hand-over before the drain resumes it. Defaults to two seconds.</summary>
    public TimeSpan IntentGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the outbox drain runs. Defaults to one second.</summary>
    public TimeSpan DrainPollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The number of commit-order partitions. Defaults to four, so a host polls fewer readers than a production one; a
    /// test of the partitioning itself sets the production value.
    /// </summary>
    public int PartitionCount { get; set; } = 4;

    /// <summary>
    /// Runs after the schema is created and before the silo starts — where a test appends the history a populated store
    /// would have and seeds the readers at its head with <see cref="ExecutionModelTestHost.SeedAtHeadAsync"/>, as a
    /// deployment seeds while no silo runs. Nothing runs in a grain yet: append through the event source, not by
    /// dispatching. <see langword="null"/> by default.
    /// </summary>
    public Func<ExecutionModelTestHost, Task>? BeforeStart { get; set; }

    /// <summary>How long <see cref="ExecutionModelTestHost.CreateAsync"/> waits for the silo to start. Defaults to one minute.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(1);
}
