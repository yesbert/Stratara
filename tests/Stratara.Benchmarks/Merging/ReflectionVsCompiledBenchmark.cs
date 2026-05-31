using System.Reflection;
using BenchmarkDotNet.Attributes;
using Stratara.Benchmarks.Models;
using Stratara.Shared.Reflections;

namespace Stratara.Benchmarks.Merging;

/// <summary>
/// Compares the framework's real internal property-access path — a strongly-typed compiled
/// delegate from <see cref="PropertyAccessorCache"/> — against reflection
/// (<c>PropertyInfo.SetValue</c> / <c>PropertyInfo.GetValue</c>) and a direct assignment
/// baseline. The <see cref="PropertyInfo"/> is cached in <see cref="Setup"/>, so the reflection
/// rows measure the pure invoke cost, not member lookup.
/// </summary>
[MemoryDiagnoser]
public class ReflectionVsCompiledBenchmark
{
    private const string PropertyName = "Currency";
    private readonly string _value = "USD";
    private Treaty _treaty = null!;
    private PropertyInfo _propertyInfo = null!;
    private Action<Treaty, string> _compiledSetter = null!;
    private Func<Treaty, string> _compiledGetter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _treaty = new Treaty();
        _propertyInfo = typeof(Treaty).GetProperty(PropertyName)!;
        _compiledSetter = PropertyAccessorCache.GetOrCreateSetter<Treaty, string>(PropertyName);
        _compiledGetter = PropertyAccessorCache.GetOrCreateGetter<Treaty, string>(PropertyName);
    }

    [Benchmark(Baseline = true)]
    public void Set_Direct() => _treaty.Currency = _value;

    [Benchmark]
    public void Set_Reflection() => _propertyInfo.SetValue(_treaty, _value);

    [Benchmark]
    public void Set_CompiledDelegate() => _compiledSetter(_treaty, _value);

    [Benchmark]
    public string Get_Reflection() => (string)_propertyInfo.GetValue(_treaty)!;

    [Benchmark]
    public string Get_CompiledDelegate() => _compiledGetter(_treaty);
}
