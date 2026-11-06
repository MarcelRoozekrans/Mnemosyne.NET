using BenchmarkDotNet.Running;
using Mnemosyne.NET.Benchmarks;

Type[] benchmarks = [typeof(RankingBenchmarks)];
BenchmarkSwitcher.FromTypes(benchmarks).Run(args);
