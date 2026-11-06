using BenchmarkDotNet.Running;
using Mnemosyne.NET.Benchmarks;

List<Type> benchmarks = [typeof(RankingBenchmarks), typeof(MiddlewareBenchmarks)];
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SearchBenchmarks.ConnectionVariable)))
{
    Console.WriteLine($"Skipping {nameof(SearchBenchmarks)}: {SearchBenchmarks.ConnectionVariable} is not set.");
}
else
{
    benchmarks.Add(typeof(SearchBenchmarks));
}

BenchmarkSwitcher.FromTypes([.. benchmarks]).Run(args);
