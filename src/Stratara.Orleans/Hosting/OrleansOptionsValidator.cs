using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Orleans.Singleton;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Validates every settings type the execution model binds when the host starts, so an invalid setting fails
/// the start and names itself instead of stalling a reader, polling forever or re-running a command:
/// batch sizes, limits and partition counts are positive, polls and windows are longer than zero, a period
/// the runtime keeps as a reminder is at or above the runtime's minimum reminder period, and a command's grace
/// is longer than the window in which its completion is collected.
/// </summary>
internal sealed class OrleansOptionsValidator(IOptions<ReminderOptions> reminders) :
    IValidateOptions<OrleansDispatchOptions>,
    IValidateOptions<HeavyWorkOptions>,
    IValidateOptions<DurableTimerOptions>,
    IValidateOptions<ProjectionGrainOptions>,
    IValidateOptions<SagaGrainOptions>,
    IValidateOptions<CommitOrderOptions>,
    IValidateOptions<SingletonWorkOptions>,
    IValidateOptions<OutboxDrainOptions>
{
    private TimeSpan MinimumReminderPeriod => reminders.Value.MinimumReminderPeriod;

    /// <summary>Validates <typeparamref name="TOptions"/> when the host starts.</summary>
    public static void Register<TOptions>(IServiceCollection services) where TOptions : class
    {
        services.AddOptions<TOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IValidateOptions<TOptions>), typeof(OrleansOptionsValidator)));
    }

    public ValidateOptionsResult Validate(string? name, OrleansDispatchOptions options) => Result<OrleansDispatchOptions>(
        Positive(options.CompletionWindow, nameof(options.CompletionWindow)),
        options.CompletionWindow <= IntentCompletionQueue.LongestWindow
            ? null
            : $"{nameof(options.CompletionWindow)} ({options.CompletionWindow}) must be at most {IntentCompletionQueue.LongestWindow}.",
        Positive(options.CompletionBatchSize, nameof(options.CompletionBatchSize)),
        options.IntentGrace > options.CompletionWindow
            ? null
            : $"{nameof(options.IntentGrace)} ({options.IntentGrace}) must be longer than {nameof(options.CompletionWindow)} ({options.CompletionWindow}), or a command whose completion is still being collected is handed over again.");

    public ValidateOptionsResult Validate(string? name, HeavyWorkOptions options) => Result<HeavyWorkOptions>(
        Positive(options.ClusterWideLimit, nameof(options.ClusterWideLimit)),
        Positive(options.PermitRetry, nameof(options.PermitRetry)),
        Positive(options.PermitLease, nameof(options.PermitLease)));

    public ValidateOptionsResult Validate(string? name, DurableTimerOptions options) => Result<DurableTimerOptions>(
        AtLeastReminderMinimum(options.RetryPeriod, nameof(options.RetryPeriod)),
        options.DueTolerance >= TimeSpan.Zero && options.DueTolerance < options.RetryPeriod
            ? null
            : $"{nameof(options.DueTolerance)} ({options.DueTolerance}) must be at least zero and shorter than {nameof(options.RetryPeriod)} ({options.RetryPeriod}).");

    public ValidateOptionsResult Validate(string? name, ProjectionGrainOptions options) => Result<ProjectionGrainOptions>(
        Positive(options.BatchSize, nameof(options.BatchSize)),
        Positive(options.PollInterval, nameof(options.PollInterval)),
        AtLeastReminderMinimum(options.KeepAlivePeriod, nameof(options.KeepAlivePeriod)));

    public ValidateOptionsResult Validate(string? name, SagaGrainOptions options) => Result<SagaGrainOptions>(
        Positive(options.BatchSize, nameof(options.BatchSize)),
        Positive(options.PollInterval, nameof(options.PollInterval)),
        AtLeastReminderMinimum(options.KeepAlivePeriod, nameof(options.KeepAlivePeriod)));

    public ValidateOptionsResult Validate(string? name, CommitOrderOptions options) => Result<CommitOrderOptions>(
        Positive(options.PartitionCount, nameof(options.PartitionCount)));

    public ValidateOptionsResult Validate(string? name, SingletonWorkOptions options) => Result<SingletonWorkOptions>(
        AtLeastReminderMinimum(options.KeepAlivePeriod, nameof(options.KeepAlivePeriod)));

    public ValidateOptionsResult Validate(string? name, OutboxDrainOptions options) => Result<OutboxDrainOptions>(
        Positive(options.PollingInterval, nameof(options.PollingInterval)),
        Positive(options.BatchSize, nameof(options.BatchSize)));

    private static string? Positive(int value, string setting) =>
        value > 0 ? null : $"{setting} ({value}) must be at least 1.";

    private static string? Positive(TimeSpan value, string setting) =>
        value > TimeSpan.Zero ? null : $"{setting} ({value}) must be longer than zero.";

    private string? AtLeastReminderMinimum(TimeSpan value, string setting) =>
        value >= MinimumReminderPeriod
            ? null
            : $"{setting} ({value}) is kept as a reminder and must be at least the runtime's minimum reminder period ({MinimumReminderPeriod}).";

    private static ValidateOptionsResult Result<TOptions>(params string?[] failures)
    {
        var named = failures.OfType<string>().Select(failure => $"{typeof(TOptions).Name}.{failure}").ToList();
        return named.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(named);
    }
}
