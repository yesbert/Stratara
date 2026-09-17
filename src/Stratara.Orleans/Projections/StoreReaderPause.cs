namespace Stratara.Orleans.Projections;

/// <summary>
/// Pausing a set of store readers, and resuming exactly what was paused. A pause that fails — a partition whose
/// batch outlives the response timeout is enough — would otherwise leave the partitions that did pause with a
/// pauser nobody releases, and since rebuilds count their pausers no later rebuild clears it: the partition stops
/// reading until its silo restarts.
/// </summary>
internal static class StoreReaderPause
{
    /// <summary>
    /// Pauses every reader and returns those that paused — a call that fails where it is made counts as a failed
    /// pause like one that faults. Where one fails, the others are resumed before it throws, so a caller that sees
    /// the exception holds nothing.
    /// </summary>
    /// <param name="grains">The readers to pause.</param>
    /// <returns>The readers that paused, for the caller to resume.</returns>
    /// <exception cref="InvalidOperationException">A reader could not be paused; the message names how many.</exception>
    public static async Task<IReadOnlyList<IProjectionGrain>> PauseAllAsync(IReadOnlyList<IProjectionGrain> grains)
    {
        var pausing = new List<(IProjectionGrain Grain, Task Pause)>(grains.Count);
        var failures = new List<Exception>();
        foreach (var grain in grains)
        {
            try
            {
                pausing.Add((grain, grain.PauseAsync()));
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        var paused = new List<IProjectionGrain>(grains.Count);
        foreach (var (grain, pause) in pausing)
        {
            try
            {
                await pause;
                paused.Add(grain);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 0)
        {
            return paused;
        }

        await ResumeQuietlyAsync(paused);
        throw new InvalidOperationException(
            $"{failures.Count} of {grains.Count} store readers could not be paused, so nothing was changed and the readers that had paused were resumed.",
            failures[0]);
    }

    /// <summary>Resumes every reader, and reports what could not be resumed.</summary>
    /// <param name="grains">The readers to resume.</param>
    /// <returns>A task that completes when every reader has been asked to resume.</returns>
    /// <exception cref="InvalidOperationException">A reader could not be resumed and stays paused; the message names how many.</exception>
    public static async Task ResumeAllAsync(IReadOnlyList<IProjectionGrain> grains)
    {
        var failures = await ResumeQuietlyAsync(grains);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failures.Count} of {grains.Count} store readers stayed paused: a reader that is not resumed reads nothing until its silo is restarted.",
                failures[0]);
        }
    }

    /// <summary>Resumes every reader and returns what failed, for a caller that is already carrying an exception.</summary>
    public static async Task<IReadOnlyList<Exception>> ResumeQuietlyAsync(IReadOnlyList<IProjectionGrain> grains)
    {
        var resuming = new List<Task>(grains.Count);
        var failures = new List<Exception>();
        foreach (var grain in grains)
        {
            try
            {
                resuming.Add(grain.ResumeAsync());
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        foreach (var resume in resuming)
        {
            try
            {
                await resume;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        return failures;
    }
}
