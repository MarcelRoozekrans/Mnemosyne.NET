using System.Collections.Concurrent;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>
/// A hand-written <see cref="IReasonletMemory"/> that records every recall query and capture, and replies with what the test
/// configures: a handler per call, or an exception. Recall, remember and erase are configurable, and the rest throws.
/// </summary>
internal sealed class FakeReasonletMemory : IReasonletMemory
{
    private readonly ConcurrentQueue<ReasonletQuery> _queries = new();
    private readonly ConcurrentQueue<ReasonletCapture> _captures = new();
    private int _inFlight;
    private int _maxConcurrentCalls;

    /// <summary>Gets or sets the reply to each recall. The default is an empty success.</summary>
    public Func<ReasonletQuery, CancellationToken, Task<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>> RecallHandler { get; set; } =
        (_, _) => Task.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success([]));

    /// <summary>Gets or sets the reply to each remember. The default throws, so a test that expects captures configures it.</summary>
    public Func<ReasonletCapture, CancellationToken, Task<Result<ReasonletReceipt, MnemosyneError>>> RememberHandler { get; set; } =
        (_, _) => throw new InvalidOperationException("No RememberHandler configured.");

    /// <summary>Gets or sets the reply to each erase. The default throws, so a test that erases configures it.</summary>
    public Func<ReasonletErasure, CancellationToken, Task<Result<ReasonletErasureResult, MnemosyneError>>> EraseHandler { get; set; } =
        (_, _) => throw new NotSupportedException();

    /// <summary>Gets the query of every recall, in call order.</summary>
    public IReadOnlyList<ReasonletQuery> Queries => [.. _queries];

    /// <summary>Gets every capture, in call order.</summary>
    public IReadOnlyList<ReasonletCapture> Captures => [.. _captures];

    /// <summary>Gets the largest number of recall or remember calls that were in flight at once.</summary>
    public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    /// <summary>Replies to every recall with <paramref name="matches"/>.</summary>
    public void Returns(params ReasonletMatch[] matches) =>
        RecallHandler = (_, _) => Task.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(matches));

    /// <summary>Replies to every recall with <paramref name="error"/>.</summary>
    public void Fails(MnemosyneError error) =>
        RecallHandler = (_, _) => Task.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(error));

    /// <summary>Throws <paramref name="exception"/> from every recall.</summary>
    public void Throws(Exception exception) => RecallHandler = (_, _) => throw exception;

    public async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletCapture capture, CancellationToken ct)
    {
        _captures.Enqueue(capture);
        Enter();
        try
        {
            return await RememberHandler(capture, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async ValueTask<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>> RecallAsync(ReasonletQuery query, CancellationToken ct)
    {
        _queries.Enqueue(query);
        Enter();
        try
        {
            return await RecallHandler(query, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> RecordExecutionAsync(ReasonletRun run, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<UnitResult<MnemosyneError>> RecordFeedbackAsync(ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct) =>
        new(EraseHandler(erasure, ct));

    public ValueTask<UnitResult<MnemosyneError>> ApproveAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<UnitResult<MnemosyneError>> RejectAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<Result<IReadOnlyList<ReasonletReview>, MnemosyneError>> ListPendingAsync(ReasonletScope scope, int limit, ReasonletId? after, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask<Result<IReadOnlyList<ReasonletRecord>, MnemosyneError>> ListSubjectReasonletsAsync(ReasonletScope scope, string subjectId, int limit, ReasonletId? after, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<Result<IReadOnlyList<ReasonletExecutionRecord>, MnemosyneError>> ListSubjectRunsAsync(ReasonletScope scope, string subjectId, int limit, ExecutionId? after, CancellationToken ct) => throw new NotSupportedException();

    private void Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxConcurrentCalls)))
        {
            Interlocked.CompareExchange(ref _maxConcurrentCalls, now, seen);
        }
    }
}
