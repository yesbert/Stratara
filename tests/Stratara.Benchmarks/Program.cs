using BenchmarkDotNet.Running;
using Stratara.Benchmarks.Security;

// BenchmarkRunner.Run<LoggerExtensionsBenchmark>();
// BenchmarkRunner.Run<SmartMergerBenchmark>();
// BenchmarkRunner.Run<PropertySetterBenchmark>();
// BenchmarkRunner.Run<EventStreamBenchmark>();
// BenchmarkRunner.Run<EventStreamHashing>();
// BenchmarkRunner.Run<HashComputeBenchmarks>();
BenchmarkRunner.Run<SecureJsonSerializerBenchmark>();