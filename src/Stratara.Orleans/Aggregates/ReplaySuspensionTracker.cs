using Microsoft.Extensions.Logging;
using Stratara.Orleans.Diagnostics;

namespace Stratara.Orleans.Aggregates;

/// <summary>
/// Remembers, between the drain's runs, whether recorded commands are being held back by a full replay, so the hold is
/// logged once when it begins and once when it ends rather than on every run. The drain's work is resolved per run and
/// the dispatcher per scope, so the flag lives here, one per silo.
/// </summary>
internal sealed class ReplaySuspensionTracker
{
    private int _holding;

    /// <summary>A run skipped the resumption because a replay is active; logs only the first of a series.</summary>
    public void HeldBack(ILogger logger)
    {
        if (Interlocked.Exchange(ref _holding, 1) == 0)
        {
            logger.LogResumeHeldBackByReplay();
        }
    }

    /// <summary>A run resumes commands; logs the release only where a hold was logged before it.</summary>
    public void Released(ILogger logger)
    {
        if (Interlocked.Exchange(ref _holding, 0) == 1)
        {
            logger.LogResumeReleasedAfterReplay();
        }
    }
}
