using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Stratara.Shared.Reflections;

/// <summary>
/// Process-wide cache of compiled property-getter and -setter delegates plus their
/// <see cref="PropertyInfo"/>. Avoids the per-call overhead of reflection on hot paths such as the
/// merge primitives, event-mapper factory, and aggregate field-change application.
/// </summary>
/// <remarks>
/// All cache stores are unbounded. The framework assumes a finite set of <c>(Type, PropertyName)</c>
/// pairs per process.
/// </remarks>
public static class PropertyAccessorCache
{
    private static readonly ConcurrentDictionary<(Type, string), Delegate> s_getterCache = new();
    private static readonly ConcurrentDictionary<(Type, string), Delegate> s_setterCache = new();
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> s_propertyInfoCache = new();

    /// <summary>
    /// Returns a cached or freshly-compiled getter delegate for the named property on
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Type that declares the property.</typeparam>
    /// <typeparam name="TProp">CLR type of the property's value.</typeparam>
    /// <param name="propertyName">Name of the public instance property.</param>
    /// <returns>A strongly-typed getter delegate.</returns>
    /// <exception cref="ArgumentException">Thrown when the property is not found on <typeparamref name="T"/>.</exception>
    public static Func<T, TProp> GetOrCreateGetter<T, TProp>(string propertyName)
    {
        var key = (typeof(T), propertyName);
        if (s_getterCache.TryGetValue(key, out var del))
        {
            return (Func<T, TProp>)del;
        }

        var prop = GetOrCachePropertyInfo(typeof(T), propertyName);
        var param = Expression.Parameter(typeof(T), "x");
        var body = Expression.Property(param, prop);
        var lambda = Expression.Lambda<Func<T, TProp>>(body, param);
        var compiled = lambda.Compile();

        s_getterCache.TryAdd(key, compiled);
        return compiled;
    }

    /// <summary>
    /// Returns a cached or freshly-compiled setter delegate for the named property on
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Type that declares the property.</typeparam>
    /// <typeparam name="TProp">CLR type of the property's value.</typeparam>
    /// <param name="propertyName">Name of the public instance property.</param>
    /// <returns>A strongly-typed setter delegate.</returns>
    /// <exception cref="ArgumentException">Thrown when the property is not found on <typeparamref name="T"/>.</exception>
    public static Action<T, TProp> GetOrCreateSetter<T, TProp>(string propertyName)
    {
        var key = (typeof(T), propertyName);
        if (s_setterCache.TryGetValue(key, out var del))
        {
            return (Action<T, TProp>)del;
        }

        var prop = GetOrCachePropertyInfo(typeof(T), propertyName);
        var target = Expression.Parameter(typeof(T), "target");
        var value = Expression.Parameter(typeof(TProp), "value");
        var assign = Expression.Assign(Expression.Property(target, prop), value);
        var lambda = Expression.Lambda<Action<T, TProp>>(assign, target, value);
        var compiled = lambda.Compile();

        s_setterCache.TryAdd(key, compiled);
        return compiled;
    }

    /// <summary>
    /// Sets the named property on <paramref name="target"/> using the cached strongly-typed setter
    /// for <typeparamref name="TTarget"/> / <typeparamref name="TProperty"/>.
    /// </summary>
    /// <typeparam name="TTarget">Type that declares the property.</typeparam>
    /// <typeparam name="TProperty">CLR type of the property's value.</typeparam>
    /// <param name="target">Instance to mutate.</param>
    /// <param name="propertyName">Name of the public instance property.</param>
    /// <param name="value">New property value.</param>
    public static void SetValueByName<TTarget, TProperty>(TTarget target, string propertyName, TProperty value)
    {
        var setter = GetOrCreateSetter<TTarget, TProperty>(propertyName);
        setter(target, value);
    }

    /// <summary>
    /// Untyped-value overload of <see cref="SetValueByName{TTarget,TProperty}"/>: the property type
    /// is resolved from the cached <see cref="PropertyInfo"/> and the typed setter is then invoked
    /// via reflection. Used when the caller only knows the property name (e.g. apply-handler for
    /// generic <c>FieldChangedEvent</c>).
    /// </summary>
    /// <typeparam name="TTarget">Type that declares the property.</typeparam>
    /// <param name="target">Instance to mutate.</param>
    /// <param name="propertyName">Name of the public instance property.</param>
    /// <param name="value">New property value, boxed as <see cref="object"/>.</param>
    /// <exception cref="ArgumentException">Thrown when the property is not found on <typeparamref name="TTarget"/>.</exception>
    public static void SetValueByName<TTarget>(TTarget target, string propertyName, object? value)
    {
        var prop = GetOrCachePropertyInfo(typeof(TTarget), propertyName);
        var method = typeof(PropertyAccessorCache)
            .GetMethod(nameof(GetOrCreateSetter))!
            .MakeGenericMethod(typeof(TTarget), prop.PropertyType);
        var setter = (Delegate)method.Invoke(null, new object[] { propertyName })!;
        setter.DynamicInvoke(target, value);
    }

    /// <summary>
    /// Reads the named property from <paramref name="target"/> using the cached strongly-typed
    /// getter.
    /// </summary>
    /// <typeparam name="TTarget">Type that declares the property.</typeparam>
    /// <typeparam name="TProperty">CLR type of the property's value.</typeparam>
    /// <param name="target">Instance to read.</param>
    /// <param name="propertyName">Name of the public instance property.</param>
    /// <returns>The current property value.</returns>
    public static TProperty GetValueByName<TTarget, TProperty>(TTarget target, string propertyName)
    {
        var getter = GetOrCreateGetter<TTarget, TProperty>(propertyName);
        return getter(target);
    }

    private static PropertyInfo GetOrCachePropertyInfo(Type type, string propertyName)
    {
        var key = (type, propertyName);
        if (s_propertyInfoCache.TryGetValue(key, out var info))
        {
            return info;
        }

        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null)
        {
            throw new ArgumentException($"Property '{propertyName}' not found on type '{type.Name}'");
        }

        s_propertyInfoCache.TryAdd(key, prop);
        return prop;
    }
}
