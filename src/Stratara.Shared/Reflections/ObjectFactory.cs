using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Stratara.Shared.Reflections;

/// <summary>
/// Parameterless-constructor instantiation cache. Compiles a <c>new T()</c> lambda per requested
/// type and reuses it for subsequent calls, avoiding the cost of repeated reflection / activation.
/// </summary>
/// <remarks>
/// The factory cache is unbounded and lives for the lifetime of the process; assumes a finite set
/// of types per host.
/// </remarks>
public static class ObjectFactory
{
    private static readonly ConcurrentDictionary<Type, Func<object>> s_factoryCache = new();

    /// <summary>
    /// Creates a new instance of <paramref name="type"/> using its parameterless constructor.
    /// </summary>
    /// <param name="type">The CLR type to instantiate.</param>
    /// <returns>A freshly-constructed instance, boxed as <see cref="object"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is <see langword="null"/>.</exception>
    public static object CreateInstance(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return s_factoryCache.GetOrAdd(type, static t =>
        {
            var ctor = Expression.New(t);
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(ctor, typeof(object)));
            return lambda.Compile();
        })();
    }

    /// <summary>
    /// Strongly-typed counterpart to <see cref="CreateInstance(Type)"/>.
    /// </summary>
    /// <typeparam name="T">The type to instantiate. Must have an accessible parameterless constructor.</typeparam>
    /// <returns>A freshly-constructed <typeparamref name="T"/> instance.</returns>
    public static T CreateInstance<T>() where T : new()
        => (T)CreateInstance(typeof(T));
}
