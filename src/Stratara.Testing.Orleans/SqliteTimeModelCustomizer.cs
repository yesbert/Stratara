using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stratara.Testing.Orleans;

/// <summary>
/// Stores every point in time of the test host's write context as a number SQLite compares and orders the way the
/// instants compare, so the intent store's due query, its order and its claims run on SQLite as on a production store.
/// SQLite keeps a point in time as text otherwise, which EF Core refuses to compare. The number keeps a tenth of a
/// millisecond, below the millisecond the intent store stamps.
/// </summary>
internal sealed class SqliteTimeModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
