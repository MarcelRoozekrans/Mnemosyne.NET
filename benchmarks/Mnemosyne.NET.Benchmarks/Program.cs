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

// BenchmarkDotNet exits 0 even when a benchmark throws in setup, so read the summaries and fail the run here.
// No summaries also counts as a failure: a filter that matches nothing, such as the search benchmark without its
// database, measures nothing.
var summaries = BenchmarkSwitcher.FromTypes([.. benchmarks]).Run(args).ToList();
var failed = summaries.Count == 0 || summaries.Any(summary =>
    summary.HasCriticalValidationErrors
    || summary.BenchmarksCases.Length == 0
    || summary.Reports.Length < summary.BenchmarksCases.Length
    || summary.Reports.Any(report => !report.Success));
return failed ? 1 : 0;
