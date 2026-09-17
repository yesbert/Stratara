namespace Stratara.Orleans.Projections;

/// <summary>
/// Pausing a set of store readers, and resuming every one that may hold a pauser. A reader counts its pauser
/// before it waits for the running loop, so a pause whose answer is lost — a partition whose batch outlives the
/// response timeout — has paused the reader all the same: what is resumed is therefore every reader the pause was
/// asked of, not only those that answered. A resume finds nothing to release where no pause was counted, so
/// resuming one reader too many costs nothing, while resuming one too few leaves it reading nothing until its silo
/// restarts.
/// </summary>
internal static class StoreReaderPause
{
    /// <summary>
    /// Pauses every reader and returns the readers to resume — every one the pause was asked of. Where a pause
    /// fails, they are resumed before this throws, so a caller that sees the exception holds nothing.
    /// </summary>
    /// <param name="readers">The readers to pause, by the name of the partition they read.</param>
    /// <returns>The readers to resume.</returns>
    /// <exception cref="InvalidOperationException">A reader could not be paused; the message names which.</exception>
    public static async Task<IReadOnlyList<PausedReader>> PauseAllAsync(IReadOnlyList<PausedReader> readers)
    {
        var pausing = new List<(PausedReader Reader, Task Pause)>(readers.Count);
        var refused = new List<string>();
        var failures = new List<Exception>();
        foreach (var reader in readers)
        {
            try
            {
                pausing.Add((reader, reader.Grain.PauseAsync()));
            }
            catch (Exception ex)
            {
                refused.Add(reader.Name);
                failures.Add(ex);
            }
        }

        foreach (var (reader, pause) in pausing)
        {
            try
            {
                await pause;
            }
            catch (Exception ex)
            {
                refused.Add(reader.Name);
                failures.Add(ex);
            }
        }

        // Every reader the call reached may hold a pauser, including one whose answer was lost.
        var toResume = pausing.Select(p => p.Reader).ToList();
        if (failures.Count == 0)
        {
            return toResume;
        }

        await ResumeQuietlyAsync(toResume);
        throw new InvalidOperationException(
            $"The store readers of {string.Join(", ", refused)} could not be paused, so nothing was changed and the readers that had paused were resumed.",
            failures[0]);
    }

    /// <summary>Resumes every reader, retrying one that fails once, and reports what stayed paused.</summary>
    /// <param name="readers">The readers to resume.</param>
    /// <returns>A task that completes when every reader has been resumed.</returns>
    /// <exception cref="InvalidOperationException">
    /// A reader stayed paused and reads nothing until its silo is restarted; the message names which.
    /// </exception>
    public static async Task ResumeAllAsync(IReadOnlyList<PausedReader> readers)
    {
        var failures = await ResumeQuietlyAsync(readers);
        if (failures.Count == 0)
        {
            return;
        }

        var retry = await ResumeQuietlyAsync([.. failures.Select(failure => failure.Reader)]);
        if (retry.Count > 0)
        {
            throw new InvalidOperationException(
                $"The work finished, but the store readers of {string.Join(", ", retry.Select(failure => failure.Reader.Name))} stayed paused: " +
                "each reads nothing until its silo is restarted.",
                retry[0].Failure);
        }
    }

    /// <summary>Resumes every reader and returns what failed, for a caller that is already carrying an exception.</summary>
    public static async Task<IReadOnlyList<(PausedReader Reader, Exception Failure)>> ResumeQuietlyAsync(IReadOnlyList<PausedReader> readers)
    {
        var resuming = new List<(PausedReader Reader, Task Resume)>(readers.Count);
        var failures = new List<(PausedReader Reader, Exception Failure)>();
        foreach (var reader in readers)
        {
            try
            {
                resuming.Add((reader, reader.Grain.ResumeAsync()));
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        foreach (var (reader, resume) in resuming)
        {
            try
            {
                await resume;
            }
            catch (Exception ex)
            {
                failures.Add((reader, ex));
            }
        }

        return failures;
    }
}

/// <summary>One store reader a rebuild or a replay pauses, and the name its failure is reported under.</summary>
internal readonly record struct PausedReader(string Name, IProjectionGrain Grain);
