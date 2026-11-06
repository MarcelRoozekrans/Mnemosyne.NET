using BenchmarkDotNet.Running;
using Mnemosyne.NET.Benchmarks;

Type[] benchmarks = [typeof(RankingBenchmarks), typeof(MiddlewareBenchmarks)];
BenchmarkSwitcher.FromTypes(benchmarks).Run(args);
