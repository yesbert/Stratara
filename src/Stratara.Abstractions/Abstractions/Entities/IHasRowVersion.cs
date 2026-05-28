namespace Stratara.Abstractions.Entities;

/// <summary>
/// Optimistic-concurrency token mapped onto PostgreSQL's <c>xmin</c> system column by Npgsql.
/// EF Core uses it to detect concurrent writes — <see cref="System.Data.DBConcurrencyException"/>
/// surfaces as <c>DbUpdateConcurrencyException</c> on conflict.
/// </summary>
/// <remarks>
/// No real column is created in the schema; Npgsql maps the property to the system column.
/// Required on every projection view so parallel projection writes stay safe — without an
/// optimistic-concurrency token, a slower projection-worker can overwrite a faster one's
/// committed result with stale state.
/// </remarks>
public interface IHasRowVersion
{
    /// <summary>Current row version as read from <c>xmin</c>. Set by EF Core on save.</summary>
    uint RowVersion { get; set; }
}
