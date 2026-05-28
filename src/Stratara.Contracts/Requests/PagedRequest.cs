using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Stratara.Contracts.Requests;

/// <summary>
/// Standard pagination parameters carried on read-side queries. Nullable so callers can omit them and let
/// the defaults (<see cref="DefaultPage"/>, <see cref="DefaultPageSize"/>) apply.
/// </summary>
/// <remarks>
/// Property names are pinned via <see cref="JsonPropertyNameAttribute"/> so the wire format is independent of
/// any consumer-side <c>JsonSerializerOptions.PropertyNamingPolicy</c>.
/// </remarks>
/// <param name="Page">1-based page index. Defaults to <see cref="DefaultPage"/> when null.</param>
/// <param name="PageSize">Maximum number of items per page. Defaults to <see cref="DefaultPageSize"/> when null.</param>
[ExcludeFromCodeCoverage]
public sealed record PagedRequest(
    [property: JsonPropertyName("Page")] int? Page = 1,
    [property: JsonPropertyName("PageSize")] int? PageSize = 100)
{
    /// <summary>Default 1-based page index when none is supplied.</summary>
    public const int DefaultPage = 1;

    /// <summary>Default page-size cap when none is supplied.</summary>
    public const int DefaultPageSize = 100;
}
