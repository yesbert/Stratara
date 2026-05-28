using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Stratara.Benchmarks;

public class EventStreamEntry
{
    public byte[] PreviousHash { get; set; } = new byte[32];
    public long SequenceNumber { get; set; }
    public long Version { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string EventTypeName { get; set; } = "TestEvent";
    public string DataJson { get; set; } = new('x', 1000);
}

[MemoryDiagnoser]
public class HashComputeBenchmarks
{
    private EventStreamEntry _entry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entry = new EventStreamEntry
        {
            PreviousHash = SHA256.HashData(Encoding.UTF8.GetBytes("seed")),
            SequenceNumber = 42,
            Version = 7,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    [Benchmark(Baseline = true)]
    public byte[] StringBuilderVersion()
    {
        var sb = new StringBuilder();
        sb.Append(Convert.ToHexString(_entry.PreviousHash)).Append('|')
            .Append(_entry.SequenceNumber).Append('|')
            .Append(_entry.Version).Append('|')
            .Append(_entry.Timestamp.ToUnixTimeMilliseconds()).Append('|')
            .Append(_entry.EventTypeName).Append('|')
            .Append(_entry.DataJson);
        var raw = sb.ToString();
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    [Benchmark]
    public byte[] StringConcatVersion()
    {
        var raw = string.Concat(
            Convert.ToHexString(_entry.PreviousHash), "|",
            _entry.SequenceNumber, "|",
            _entry.Version, "|",
            _entry.Timestamp.ToUnixTimeMilliseconds(), "|",
            _entry.EventTypeName, "|",
            _entry.DataJson);

        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    [Benchmark]
    public byte[] SpanWriterVersion()
    {
        var buffer = new ArrayBufferWriter<byte>();

        Write(Convert.ToHexString(_entry.PreviousHash));
        Write("|");
        Write(_entry.SequenceNumber.ToString());
        Write("|");
        Write(_entry.Version.ToString());
        Write("|");
        Write(_entry.Timestamp.ToUnixTimeMilliseconds().ToString());
        Write("|");
        Write(_entry.EventTypeName);
        Write("|");
        Write(_entry.DataJson);

        return SHA256.HashData(buffer.WrittenSpan.ToArray());

        void Write(string s) => buffer.Write(Encoding.UTF8.GetBytes(s));
    }
}