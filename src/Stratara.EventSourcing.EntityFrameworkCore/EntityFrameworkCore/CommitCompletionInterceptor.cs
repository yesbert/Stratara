using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Stratara.EventSourcing.EntityFrameworkCore;

/// <summary>
/// Lets a commit, once begun, run to its end whatever the caller's cancellation says. A database driver that is told
/// to cancel while it waits for a commit to be acknowledged may report the cancellation after the database committed;
/// the caller then takes a committed save for one that failed, and whatever runs it — a transport, a retry, a resumed
/// command — runs it again and records the same facts twice. With this interceptor a cancellation is honoured while
/// the changes are written, which leaves nothing behind, and no longer once the commit has begun, so what the caller
/// is told is what happened. A save that would run as a single statement outside a transaction — committed by the
/// database the moment the statement ends, with no commit to let run — is given a transaction as well.
/// </summary>
/// <remarks>
/// The framework adds it to every context it registers. A context a host registers itself, and that the framework
/// writes through, should add it too:
/// <code>
/// services.AddDbContextFactory&lt;AppWriteDbContext&gt;(options => options
///     .UseNpgsql(connectionString)
///     .AddInterceptors(CommitCompletionInterceptor.Instance));
/// </code>
/// Add it after any transaction interceptor that must act before the commit: it performs the commit in its own
/// <c>TransactionCommittingAsync</c>, and an interceptor that runs after it sees the commit already done. A commit is
/// bounded by the connection's own command timeout (with Npgsql, <c>Command Timeout</c> in the connection string). A
/// context whose <c>AutoTransactionBehavior</c> was set to <c>Never</c> keeps it, and none of its saves are covered. The
/// framework's unit of work reads whether a context's options carry this interceptor: on one that does not, it saves
/// without the caller's token at all, so that save runs to its end whole. A host that enables a retrying
/// execution strategy should know that the strategy runs a save again when the acknowledgement of its commit was lost;
/// an append then fails as a concurrency conflict although it committed.
/// </remarks>
public sealed class CommitCompletionInterceptor : IDbTransactionInterceptor, ISaveChangesInterceptor
{
    private CommitCompletionInterceptor()
    {
    }

    /// <summary>The interceptor; it holds no state, so one instance serves every context.</summary>
    public static CommitCompletionInterceptor Instance { get; } = new();

    /// <inheritdoc/>
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        InTransaction(eventData.Context);
        return result;
    }

    /// <inheritdoc/>
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        InTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.IsSuppressed)
        {
            return result;
        }

        await transaction.CommitAsync(CancellationToken.None);
        return InterceptionResult.Suppress();
    }

    private static void InTransaction(DbContext? context)
    {
        if (context?.Database is { AutoTransactionBehavior: AutoTransactionBehavior.WhenNeeded } database)
        {
            database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }
    }
}
