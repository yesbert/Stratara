using System.Linq.Expressions;

namespace Stratara.EventSourcing.EntityFrameworkCore.Extensions;

/// <summary>
/// LINQ extensions that translate a property name supplied as a string into a typed
/// <c>OrderBy</c> / <c>OrderByDescending</c> projection — useful for dynamic sorting
/// driven by paged-query parameters.
/// </summary>
public static class LinqExtensions
{
    /// <summary>
    /// Orders the source ascending by the property named <paramref name="propertyName"/>.
    /// </summary>
    /// <typeparam name="T">The element type of the queryable.</typeparam>
    /// <param name="source">The source queryable.</param>
    /// <param name="propertyName">The CLR property name to sort by.</param>
    /// <returns>The ordered queryable.</returns>
    public static IOrderedQueryable<T> OrderBy<T>(this IQueryable<T> source, string propertyName) =>
        source.OrderBy(ToLambda<T>(propertyName));

    /// <summary>
    /// Orders the source descending by the property named <paramref name="propertyName"/>.
    /// </summary>
    /// <typeparam name="T">The element type of the queryable.</typeparam>
    /// <param name="source">The source queryable.</param>
    /// <param name="propertyName">The CLR property name to sort by.</param>
    /// <returns>The ordered queryable.</returns>
    public static IOrderedQueryable<T> OrderByDescending<T>(this IQueryable<T> source, string propertyName) =>
        source.OrderByDescending(ToLambda<T>(propertyName));

    private static Expression<Func<T, object>> ToLambda<T>(string propertyName)
    {
        var parameter = Expression.Parameter(typeof(T));
        var property = Expression.Property(parameter, propertyName);
        var propAsObject = Expression.Convert(property, typeof(object));

        return Expression.Lambda<Func<T, object>>(propAsObject, parameter);
    }
}
