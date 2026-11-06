using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Stratara.Identity.EntityFrameworkCore;

internal static class TrackedDeleteExtensions
{
    /// <summary>
    /// Deletes in the store and forgets the tracked copies of what was deleted: on a shared context a copy left behind
    /// would conflict with the next entry written under its key. A pending add is left alone, because it has no row
    /// here to delete and is the consumer's to save; detection stays off so that nothing else of theirs is disturbed.
    /// </summary>
    public static async Task DeleteAndForgetAsync<TEntry>(
        this DbContext context, Expression<Func<TEntry, bool>> predicate, CancellationToken cancellationToken)
        where TEntry : class
    {
        await context.Set<TEntry>().Where(predicate).ExecuteDeleteAsync(cancellationToken);

        var autoDetect = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var deleted = predicate.Compile();
            var tracked = context.ChangeTracker.Entries<TEntry>()
                .Where(e => e.State != EntityState.Added && deleted(e.Entity))
                .ToList();
            foreach (var entry in tracked)
            {
                entry.State = EntityState.Detached;
            }
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }
}
