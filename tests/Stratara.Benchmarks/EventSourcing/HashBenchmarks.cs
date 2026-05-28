using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Stratara.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class EventStreamHashing
{
    private static readonly SHA256 Sha = SHA256.Create();
    private byte[] _payload10K = null!;
    private byte[] _payload1K = null!;
    private byte[] _payload256 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload256 = BuildPayload(256);
        _payload1K = BuildPayload(1024);
        _payload10K = BuildPayload(10 * 1024);
    }

    private static byte[] BuildPayload(int size)
    {
        // Dummy JSON-like payload
        var json = new string('x', size);
        var raw = $"0|1|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|TestEvent|{json}";
        return Encoding.UTF8.GetBytes(raw);
    }

    [Benchmark(Baseline = true)]
    public byte[] Sha256_256B_NewInstance()
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(_payload256);
    }

    [Benchmark]
    public byte[] Sha256_256B_Reused()
    {
        lock (Sha)
        {
            return Sha.ComputeHash(_payload256);
        }
    }

    [Benchmark]
    public byte[] Sha256_1KB_NewInstance()
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(_payload1K);
    }

    [Benchmark]
    public byte[] Sha256_1KB_Reused()
    {
        lock (Sha)
        {
            return Sha.ComputeHash(_payload1K);
        }
    }

    [Benchmark]
    public byte[] Sha256_10KB_NewInstance()
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(_payload10K);
    }

    [Benchmark]
    public byte[] Sha256_10KB_Reused()
    {
        lock (Sha)
        {
            return Sha.ComputeHash(_payload10K);
        }
    }
}