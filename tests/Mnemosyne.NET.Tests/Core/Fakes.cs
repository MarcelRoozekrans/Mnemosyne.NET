using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>A store that records the facade's calls and answers with configurable results.</summary>
internal sealed class FakeStore : IReasonletStore
{
    public static readonly ReasonletReceipt Receipt =
        new(ReasonletId.New(), "1.0", ExecutionId.New(), ReasonletCaptureKind.NewReasonlet, ReasonletStatus.Active);

    public int Calls { get; private set; }

    public ReasonletStoreEntry? Remembered { get; private set; }

    public Result<ReasonletReceipt, MnemosyneError> RememberResult { get; set; } =
        Result<ReasonletReceipt, MnemosyneError>.Success(Receipt);

    public (ReasonletScope Scope, float[] Embedding, string? Intent, int Limit)? Searched { get; private set; }

    public Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError> SearchResult { get; set; } =
        Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([]);

    public (ReasonletScope Scope, ReasonletId Id, ReasonletOutcome Outcome, ReasonletProvenance Provenance, CancellationToken Ct)? Executed { get; private set; }

    public Result<ExecutionId, MnemosyneError> ExecutionResult { get; set; } =
        Result<ExecutionId, MnemosyneError>.Success(ExecutionId.New());

    public (ReasonletScope Scope, ExecutionId Execution, ReasonletFeedback Feedback, CancellationToken Ct)? Fed { get; private set; }

    public UnitResult<MnemosyneError> FeedbackResult { get; set; } = UnitResult<MnemosyneError>.Success();

    public (ReasonletErasure Erasure, CancellationToken Ct)? Erased { get; private set; }

    public Result<ReasonletErasureResult, MnemosyneError> EraseResult { get; set; } =
        Result<ReasonletErasureResult, MnemosyneError>.Success(new ReasonletErasureResult { Reasonlets = 2, Executions = 3 });

    public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        Calls++;
        Remembered = entry;
        return ValueTask.FromResult(RememberResult);
    }

    public ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        Calls++;
        Searched = (scope, embedding.ToArray(), intent, limit);
        return ValueTask.FromResult(SearchResult);
    }

    public ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        Calls++;
        Executed = (scope, id, outcome, provenance, ct);
        return ValueTask.FromResult(ExecutionResult);
    }

    public ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        Calls++;
        Fed = (scope, execution, feedback, ct);
        return ValueTask.FromResult(FeedbackResult);
    }

    public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        Calls++;
        Erased = (erasure, ct);
        return ValueTask.FromResult(EraseResult);
    }

    public ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(ReasonletRetention retention, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A selector that records what it was asked to rank and answers with a configurable result.</summary>
internal sealed class FakeSelector : IReasonletSelector
{
    public int Calls { get; private set; }

    public ReasonletQuery? Query { get; private set; }

    public IReadOnlyList<ReasonletCandidate>? Candidates { get; private set; }

    public Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>? Result { get; set; }

    public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RankAsync(
        ReasonletQuery query, IReadOnlyList<ReasonletCandidate> candidates, CancellationToken ct)
    {
        Calls++;
        Query = query;
        Candidates = candidates;
        return ValueTask.FromResult(Result ?? Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
            candidates.Select(c => new ReasonletMatch(c.Reasonlet, new ReasonletScore(c.Similarity, 0.5, 1, 0.5))).ToList()));
    }
}

/// <summary>An embedding generator that records its inputs and answers with a configurable response.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public static readonly float[] Vector = [0.6f, 0.8f, 0f];

    public List<string> Inputs { get; } = [];

    public int Calls { get; private set; }

    public Func<GeneratedEmbeddings<Embedding<float>>?> Respond { get; set; } = () => new([new Embedding<float>(Vector)]);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Inputs.AddRange(values);
        return Task.FromResult(Respond()!);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An intent extractor that records its questions and answers with a configurable response.</summary>
internal sealed class FakeIntentExtractor : IIntentExtractor
{
    public List<string> Questions { get; } = [];

    public Func<Result<ReasonletIntent, MnemosyneError>> Respond { get; set; } =
        () => Result<ReasonletIntent, MnemosyneError>.Success(new ReasonletIntent("extracted", 0.9));

    public static FakeIntentExtractor Answering(string intent, double confidence) => new()
    {
        Respond = () => Result<ReasonletIntent, MnemosyneError>.Success(new ReasonletIntent(intent, confidence)),
    };

    public ValueTask<Result<ReasonletIntent, MnemosyneError>> ExtractAsync(string question, CancellationToken ct)
    {
        Questions.Add(question);
        return ValueTask.FromResult(Respond());
    }
}
