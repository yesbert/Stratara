using System.Reflection;

namespace Stratara.Shared.Reflections;

/// <summary>
/// Reflection-backed value accessor for a single property on a declaring type. Compiles
/// strongly-typed getter / setter delegates via <see cref="PropertyAccessorCache"/> and exposes
/// them through untyped <see cref="GetValue"/> / <see cref="SetValue"/> methods that take any
/// instance of the declaring type as boxed <see cref="object"/>.
/// </summary>
/// <remarks>
/// If the underlying property is read-only, <see cref="SetValue"/> is a no-op rather than throwing —
/// the caller is responsible for honouring <see cref="PropertyInfo.CanWrite"/> if assignment must
/// be observable.
/// </remarks>
public sealed class PropertyAccessor
{
    private readonly Delegate _getter;
    private readonly Delegate? _setter;

    /// <summary>
    /// Initializes a new accessor for <paramref name="property"/> declared on <paramref name="declaringType"/>.
    /// </summary>
    /// <param name="property">The property to wrap.</param>
    /// <param name="declaringType">The CLR type that declares <paramref name="property"/>.</param>
    public PropertyAccessor(PropertyInfo property, Type declaringType)
    {
        Name = property.Name;
        PropertyType = property.PropertyType;

        var getterMethod = typeof(PropertyAccessorCache)
            .GetMethod(nameof(PropertyAccessorCache.GetOrCreateGetter))!
            .MakeGenericMethod(declaringType, property.PropertyType);

        _getter = (Delegate)getterMethod.Invoke(null, [property.Name])!;

        if (!property.CanWrite)
        {
            return;
        }

        var setterMethod = typeof(PropertyAccessorCache)
            .GetMethod(nameof(PropertyAccessorCache.GetOrCreateSetter))!
            .MakeGenericMethod(declaringType, property.PropertyType);

        _setter = (Delegate)setterMethod.Invoke(null, [property.Name])!;
    }

    /// <summary>Name of the wrapped property.</summary>
    public string Name { get; }

    /// <summary>CLR type of the wrapped property's value.</summary>
    public Type PropertyType { get; }

    /// <summary>
    /// Reads the property's value from <paramref name="instance"/>.
    /// </summary>
    /// <param name="instance">An instance of the declaring type.</param>
    /// <returns>The current property value, boxed as <see cref="object"/>.</returns>
    public object? GetValue(object instance) => _getter.DynamicInvoke(instance);

    /// <summary>
    /// Writes <paramref name="value"/> into the property on <paramref name="instance"/>. No-op if
    /// the property is read-only.
    /// </summary>
    /// <param name="instance">An instance of the declaring type.</param>
    /// <param name="value">The new property value, boxed as <see cref="object"/>.</param>
    public void SetValue(object instance, object? value)
    {
        _setter?.DynamicInvoke(instance, value);
    }
}
