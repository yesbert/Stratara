using BenchmarkDotNet.Running;

// Run any subset via the command line, e.g.:
//   dotnet run -c Release -- --filter '*PropertySetterBenchmark*'
//   dotnet run -c Release -- --filter '*'                 (everything)
//   dotnet run -c Release -- --list flat                  (list available benchmarks)
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Assembly entry-point marker for the BenchmarkDotNet switcher.</summary>
public partial class Program;
