using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>A store that records the facade's calls and answers with configurable results.</summary>
internal sealed class FakeStore : IReasonletStore
{
    public static readonly ReasonletReceipt Receipt =
        new(ReasonletId.New(), "1.0", ExecutionId.New(), ReasonletCaptureKind.NewReasonlet, ReasonletStatus.Active, ReasonletVerdictSource.Agent, true);

    public static readonly ExecutionId ExecutionIdentifier = ExecutionId.New();

    public int Dimensions { get; set; } = FakeEmbeddingGenerator.Vector.Length;

    public int Calls { get; private set; }

    public ReasonletStoreEntry? Remembered { get; private set; }

    /// <summary>Gets or sets the remember's answer. When null, the fake answers <see cref="Receipt"/> with the verdict the store rule derives from the entry.</summary>
    public Result<ReasonletReceipt, MnemosyneError>? RememberResult { get; set; }

    public (ReasonletScope Scope, float[] Embedding, string? Intent, int Limit)? Searched { get; private set; }

    public Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError> SearchResult { get; set; } =
        Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([]);

    public (ReasonletScope Scope, ReasonletId Id, ReasonletOutcome Outcome, ReasonletProvenance Provenance, ReasonletEvaluation? Evaluation, CancellationToken Ct)? Executed { get; private set; }

    /// <summary>Gets or sets the append's answer. When null, the fake answers <see cref="ExecutionIdentifier"/> with the verdict the store rule derives.</summary>
    public Result<ReasonletRunReceipt, MnemosyneError>? ExecutionResult { get; set; }

    public (ReasonletScope Scope, ExecutionId Execution, ReasonletFeedback Feedback, CancellationToken Ct)? Fed { get; private set; }

    public UnitResult<MnemosyneError> FeedbackResult { get; set; } = UnitResult<MnemosyneError>.Success();

    public (ReasonletErasure Erasure, CancellationToken Ct)? Erased { get; private set; }

    public Result<ReasonletErasureResult, MnemosyneError> EraseResult { get; set; } =
        Result<ReasonletErasureResult, MnemosyneError>.Success(new ReasonletErasureResult { Reasonlets = 2, Executions = 3 });

    public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        Calls++;
        Remembered = entry;
        var (source, success) = Verdict(entry.Capture.Outcome, entry.Evaluation);
        return ValueTask.FromResult(RememberResult ?? Result<ReasonletReceipt, MnemosyneError>.Success(Receipt with { Source = source, Success = success }));
    }

    public ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        Calls++;
        Searched = (scope, embedding.ToArray(), intent, limit);
        return ValueTask.FromResult(SearchResult);
    }

    public ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct)
    {
        Calls++;
        Executed = (scope, id, outcome, provenance, evaluation, ct);
        var (source, success) = Verdict(outcome, evaluation);
        return ValueTask.FromResult(ExecutionResult ?? Result<ReasonletRunReceipt, MnemosyneError>.Success(new ReasonletRunReceipt(ExecutionIdentifier, source, success)));
    }

    /// <summary>The verdict a store derives for a new run: the evaluator's when there is one, else the agent's.</summary>
    private static (ReasonletVerdictSource Source, bool Success) Verdict(ReasonletOutcome outcome, ReasonletEvaluation? evaluation) =>
        evaluation is { } verdict ? (ReasonletVerdictSource.Evaluator, verdict.Success) : (ReasonletVerdictSource.Agent, outcome.Success);

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

    public (ReasonletScope Scope, ReasonletId Id, CancellationToken Ct)? Got { get; private set; }

    public Result<ReasonletDefinition, MnemosyneError> GetResult { get; set; } =
        Result<ReasonletDefinition, MnemosyneError>.Success(TestData.Definition());

    public ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        Calls++;
        Got = (scope, id, ct);
        return ValueTask.FromResult(GetResult);
    }

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
            candidates.Select(c => new ReasonletMatch(c.Reasonlet, new ReasonletScore(c.Similarity, 0.5, 1, 0.5), new ReasonletEvidence(c.Metrics.Runs, c.Metrics.JudgedRuns, false))).ToList()));
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

/// <summary>An evaluator that records its requests and answers with a configurable response.</summary>
internal sealed class FakeEvaluator : IReasonletEvaluator
{
    public static readonly ReasonletEvaluation Verdict = new(true, 0.75, "fake-judge");

    public List<ReasonletEvaluationRequest> Requests { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public Func<Result<ReasonletEvaluation, MnemosyneError>> Respond { get; set; } =
        () => Result<ReasonletEvaluation, MnemosyneError>.Success(Verdict);

    public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        Tokens.Add(ct);
        return ValueTask.FromResult(Respond());
    }
}

/// <summary>An evaluator that waits on the token it is given until it is cancelled, after running an optional step.</summary>
internal sealed class WaitingEvaluator(Action? onCall = null) : IReasonletEvaluator
{
    public List<CancellationToken> Tokens { get; } = [];

    public async ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        Tokens.Add(ct);
        onCall?.Invoke();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return FakeEvaluator.Verdict;
    }
}

/// <summary>A clock whose timers fire only when <see cref="Fire"/> is called, so a test decides when a timeout expires.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    /// <summary>Gets the due times every timer was created or changed with, in order.</summary>
    public List<TimeSpan> DueTimes { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Fires every live timer that has a finite due time, as if that time had passed.</summary>
    public void Fire()
    {
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _due = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.DueTimes.Add(dueTime);
            _due = dueTime;
            return !_disposed;
        }

        public void FireIfDue()
        {
            if (!_disposed && _due != Timeout.InfiniteTimeSpan)
            {
                _due = Timeout.InfiniteTimeSpan;
                callback(state);
            }
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An evaluator that ignores its token and answers with <see cref="Completion"/>'s task, which completes only when a test
/// completes it, after running an optional step.
/// </summary>
internal sealed class IgnoringEvaluator(Action? onCall = null) : IReasonletEvaluator
{
    public TaskCompletionSource<Result<ReasonletEvaluation, MnemosyneError>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls { get; private set; }

    public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        Calls++;
        onCall?.Invoke();
        return new(Completion.Task);
    }
}

/// <summary>
/// A capture guard that records the captures it inspects and answers with a configurable response, after running an
/// optional step. With <see cref="Hang"/> set it ignores its token and never answers.
/// </summary>
internal sealed class FakeCaptureGuard : ICaptureGuard
{
    public List<ReasonletCapture> Seen { get; } = [];

    public int Calls { get; private set; }

    public Func<Result<CaptureVerdict, MnemosyneError>> Respond { get; set; } = () => CaptureVerdict.Allow("fake-guard");

    /// <summary>Gets or sets whether the guard ignores its token and never answers.</summary>
    public bool Hang { get; init; }

    /// <summary>Gets or sets a step run on each call, before the guard answers or hangs.</summary>
    public Action? OnCall { get; init; }

    public ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct)
    {
        Calls++;
        Seen.Add(capture);
        OnCall?.Invoke();
        return Hang
            ? new(new TaskCompletionSource<Result<CaptureVerdict, MnemosyneError>>(TaskCreationOptions.RunContinuationsAsynchronously).Task)
            : ValueTask.FromResult(Respond());
    }
}
