using BenchmarkDotNet.Attributes;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Benchmarks;

/// <summary>Ranks a fixed list of candidates: the selector's cost without a store.</summary>
[MemoryDiagnoser]
public class RankingBenchmarks
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => BenchmarkData.Now;
    }

    private ReasonletSelector _selector = null!;
    private IReadOnlyList<ReasonletCandidate> _candidates = null!;
    private ReasonletQuery _query = null!;

    [Params(10, 100, 1000)]
    public int Candidates { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _selector = new ReasonletSelector(new ReasonletRankingOptions(), new FixedTimeProvider());
        _candidates = BenchmarkData.Candidates(Candidates);
        _query = new ReasonletQuery { Scope = BenchmarkData.Scope, Question = "question", Top = 5 };
    }

    [Benchmark]
    public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> Rank() => _selector.RankAsync(_query, _candidates, CancellationToken.None);
}
