using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Benchmarks;

/// <summary>The cost the chat middleware adds to a call, against calling the inner client directly.</summary>
[MemoryDiagnoser]
public class MiddlewareBenchmarks
{
    private static readonly ChatResponse Response = new(new ChatMessage(ChatRole.Assistant, "done"));

    private sealed class NoOpChatClient : IChatClient
    {
        /// <summary>Gets the number of messages in the last request, so setup can check the suggestion was injected.</summary>
        public int LastMessageCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessageCount = messages.Count();
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubMemory(IReadOnlyList<ReasonletMatch> matches) : IReasonletMemory
    {
        public ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RecallAsync(ReasonletQuery query, CancellationToken ct) =>
            ValueTask.FromResult<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(matches));

        public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletCapture capture, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> RecordExecutionAsync(ReasonletRun run, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<UnitResult<MnemosyneError>> RecordFeedbackAsync(ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<UnitResult<MnemosyneError>> ApproveAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<UnitResult<MnemosyneError>> RejectAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<ReasonletReview>, MnemosyneError>> ListPendingAsync(ReasonletScope scope, int limit, ReasonletId? after, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<ReasonletRecord>, MnemosyneError>> ListSubjectReasonletsAsync(ReasonletScope scope, string subjectId, int limit, ReasonletId? after, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<ReasonletExecutionRecord>, MnemosyneError>> ListSubjectRunsAsync(ReasonletScope scope, string subjectId, int limit, ExecutionId? after, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "How do I find the report?")];

    private NoOpChatClient _inner = null!;
    private IChatClient _wrapped = null!;
    private IChatClient _wrappedWithRecall = null!;

    [GlobalSetup]
    public void Setup()
    {
        _inner = new NoOpChatClient();
        // A default scope, because without one the middleware passes every call straight through. Capture is off, which
        // is the same as no queue: the middleware type is internal, so the public builder is the way to construct it.
        var options = new ReasonletChatOptions { DefaultScope = BenchmarkData.Scope, Capture = false };
        _wrapped = Build(new StubMemory([]), options);
        _wrappedWithRecall = Build(new StubMemory(Matches()), options);

        // A recall that injects nothing would make WrappedWithRecall measure the same as Wrapped, so fail the run instead.
        _wrappedWithRecall.GetResponseAsync(Messages).GetAwaiter().GetResult();
        if (_inner.LastMessageCount <= Messages.Length)
        {
            throw new InvalidOperationException("The middleware did not add the recalled suggestion to the request.");
        }
    }

    private IChatClient Build(IReasonletMemory memory, ReasonletChatOptions options)
    {
        var services = new ServiceCollection().AddSingleton(memory).BuildServiceProvider();
        return new ChatClientBuilder(_inner).UseReasonlets(options).Build(services);
    }

    // One match: the middleware uses only the first.
    private static ReasonletMatch[] Matches() =>
        BenchmarkData.Candidates(1).Select(candidate => new ReasonletMatch
        {
            LineageId = candidate.LineageId,
            Reasonlet = candidate.Reasonlet,
            Score = new ReasonletScore(0.9, 0.8, 0.7, 0.8),
            Evidence = new ReasonletEvidence { Runs = 10, JudgedRuns = 4, UsesParentPrior = false },
        }).ToArray();

    [Benchmark(Baseline = true)]
    public Task<ChatResponse> Direct() => _inner.GetResponseAsync(Messages);

    [Benchmark]
    public Task<ChatResponse> Wrapped() => _wrapped.GetResponseAsync(Messages);

    [Benchmark]
    public Task<ChatResponse> WrappedWithRecall() => _wrappedWithRecall.GetResponseAsync(Messages);
}
