using BenchmarkDotNet.Running;

if (args.Length == 0)
{
    Console.WriteLine(
        "Stratara.Orleans.Benchmarks runs only with explicit arguments, for example --filter '*Append*'. " +
        "Every run is pre-registered in openspec/changes/prove-an-orleans-execution-model/evidence/expectations.md " +
        "before it starts.");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
