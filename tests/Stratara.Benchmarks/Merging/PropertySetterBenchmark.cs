using BenchmarkDotNet.Attributes;
using Stratara.Benchmarks.Models;
using Stratara.Shared.Reflections;

namespace Stratara.Benchmarks.Merging;

[MemoryDiagnoser]
public class PropertySetterBenchmark
{
    private readonly string _newValue = "USD";
    private readonly string _propertyName = "Currency";
    private Treaty _treaty = null!;

    [GlobalSetup]
    public void Setup()
    {
        _treaty = new Treaty();

        // Optional: Cache vorbereiten
        PropertyAccessorCache.GetOrCreateSetter<Treaty, string>(_propertyName);
    }

    [Benchmark(Baseline = true)]
    public void DirectAssignment()
    {
        _treaty.Currency = _newValue;
    }

    [Benchmark]
    public void UsingPropertyInfoSetValue()
    {
        var prop = typeof(Treaty).GetProperty(_propertyName);
        prop!.SetValue(_treaty, _newValue);
    }


    [Benchmark]
    public void UsingPropertyAccessorCache()
    {
        PropertyAccessorCache.SetValueByName(_treaty, _propertyName, _newValue);
    }
}