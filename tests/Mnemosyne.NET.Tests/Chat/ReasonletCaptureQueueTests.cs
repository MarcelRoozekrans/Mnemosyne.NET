using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The bounded capture queue: its single reader, its counts and how it drains on stop and on dispose.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so its capture hears only its own counts.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletCaptureQueueTests
{
    /// <summary>A hang guard only: every wait below is released by the test itself, long before this.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnqueuedItemIsProcessed()
    {
        var gate = new Gate();
        gate.Open();
        await using var queue = Queue(gate.Memory);

        Assert.True(queue.TryEnqueue(Item("q1")));

        var capture = await gate.EnteredAsync();
        Assert.Equal("q1", capture.Question);
    }

    [Fact]
    public async Task ItemsAreProcessedOneAtATimeInOrder()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory);

        Assert.True(queue.TryEnqueue(Item("q1")));
        Assert.True(queue.TryEnqueue(Item("q2")));
        Assert.True(queue.TryEnqueue(Item("q3")));
        await gate.EnteredAsync();
        gate.Open();
        await queue.DrainAsync(Ct).WaitAsync(Guard, Ct);

        Assert.Equal(1, gate.Memory.MaxConcurrentCalls);
        Assert.Equal(["q1", "q2", "q3"], gate.Memory.Captures.Select(capture => capture.Question));
    }

    [Fact]
    public async Task FullQueueDropsAndCounts()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory, capacity: 1);
        using var telemetry = new TelemetryCapture();

        Assert.True(queue.TryEnqueue(Item("q1")));
        await gate.EnteredAsync();
        Assert.True(queue.TryEnqueue(Item("q2")));
        Assert.False(queue.TryEnqueue(Item("q3")));

        Assert.Equal(
            [CaptureTelemetry.EnqueuedOutcome, CaptureTelemetry.EnqueuedOutcome, CaptureTelemetry.DroppedFullOutcome],
            Outcomes(telemetry));
        gate.Open();
    }

    [Fact]
    public async Task StopDrainsPendingItems()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory);
        using var telemetry = new TelemetryCapture();
        Assert.True(queue.TryEnqueue(Item("q1")));
        Assert.True(queue.TryEnqueue(Item("q2")));
        Assert.True(queue.TryEnqueue(Item("q3")));
        await gate.EnteredAsync();

        var drain = queue.DrainAsync(Ct);
        Assert.False(drain.IsCompleted);
        gate.Open();
        await drain.WaitAsync(Guard, Ct);

        Assert.Equal(["q1", "q2", "q3"], gate.Memory.Captures.Select(capture => capture.Question));
        Assert.DoesNotContain(CaptureTelemetry.DroppedShutdownOutcome, Outcomes(telemetry));
    }

    [Fact]
    public async Task StopTokenCancelsAndCountsLeftovers()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory);
        using var telemetry = new TelemetryCapture();
        Assert.True(queue.TryEnqueue(Item("q1")));
        Assert.True(queue.TryEnqueue(Item("q2")));
        Assert.True(queue.TryEnqueue(Item("q3")));
        await gate.EnteredAsync();
        using var stop = new CancellationTokenSource();

        var drain = queue.DrainAsync(stop.Token);
        await stop.CancelAsync();
        await drain.WaitAsync(Guard, Ct);

        // The in-flight item was cancelled and the two behind it never ran: each of the three is counted once.
        Assert.Equal("q1", Assert.Single(gate.Memory.Captures).Question);
        Assert.True(gate.SawCancellation);
        Assert.Equal(3, Outcomes(telemetry).Count(outcome => outcome == CaptureTelemetry.DroppedShutdownOutcome));
    }

    [Fact]
    public async Task AnItemThatFinishesDespiteTheStopTokenIsNotCountedAsDropped()
    {
        var gate = new Gate(honourCancellation: false);
        await using var queue = Queue(gate.Memory);
        using var telemetry = new TelemetryCapture();
        Assert.True(queue.TryEnqueue(Item("q1")));
        Assert.True(queue.TryEnqueue(Item("q2")));
        Assert.True(queue.TryEnqueue(Item("q3")));
        await gate.EnteredAsync();
        using var stop = new CancellationTokenSource();

        var drain = queue.DrainAsync(stop.Token);
        await stop.CancelAsync();
        gate.Open();
        await drain.WaitAsync(Guard, Ct);

        Assert.Equal("q1", Assert.Single(gate.Memory.Captures).Question);
        Assert.Equal(2, Outcomes(telemetry).Count(outcome => outcome == CaptureTelemetry.DroppedShutdownOutcome));
    }

    [Fact]
    public async Task EnqueueAfterStopIsDroppedAsShutdown()
    {
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory);
        await queue.DrainAsync(Ct);
        using var telemetry = new TelemetryCapture();

        Assert.False(queue.TryEnqueue(Item("q1")));

        Assert.Equal([CaptureTelemetry.DroppedShutdownOutcome], Outcomes(telemetry));
        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task DisposeDrainsWithinDrainTimeout()
    {
        var gate = new Gate();
        var queue = Queue(gate.Memory, drainTimeout: TimeSpan.FromMilliseconds(100));
        using var telemetry = new TelemetryCapture();
        Assert.True(queue.TryEnqueue(Item("q1")));
        await gate.EnteredAsync();

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.True(gate.SawCancellation);
        Assert.Contains(CaptureTelemetry.DroppedShutdownOutcome, Outcomes(telemetry));
    }

    [Fact]
    public async Task SyncDisposeDrainsWithinDrainTimeout()
    {
        var gate = new Gate();
        var queue = Queue(gate.Memory, drainTimeout: TimeSpan.FromMilliseconds(100));
        using var telemetry = new TelemetryCapture();
        Assert.True(queue.TryEnqueue(Item("q1")));
        await gate.EnteredAsync();

        await Task.Run(queue.Dispose, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.True(gate.SawCancellation);
        Assert.Contains(CaptureTelemetry.DroppedShutdownOutcome, Outcomes(telemetry));
    }

    [Fact]
    public async Task SyncAndAsyncDisposeShareOneDrain()
    {
        var memory = CaptureTestData.Memory();
        var queue = Queue(memory);
        Assert.True(queue.TryEnqueue(Item("q1")));
        await Task.Run(queue.Dispose, Ct).WaitAsync(Guard, Ct);

        var disposed = queue.DisposeAsync();

        Assert.True(disposed.IsCompletedSuccessfully);
        Assert.Single(memory.Captures);
    }

    [Fact]
    public async Task SecondDrainReturnsAtOnce()
    {
        var memory = CaptureTestData.Memory();
        var queue = Queue(memory);
        Assert.True(queue.TryEnqueue(Item("q1")));
        await queue.DrainAsync(Ct).WaitAsync(Guard, Ct);
        using var telemetry = new TelemetryCapture();

        var again = queue.DrainAsync(Ct);
        var disposed = queue.DisposeAsync();

        Assert.True(again.IsCompletedSuccessfully);
        Assert.True(disposed.IsCompletedSuccessfully);
        Assert.Single(memory.Captures);
        Assert.Empty(Outcomes(telemetry));
    }

    [Fact]
    public async Task ADrainThatStartsWhileAnotherRunsWaitsForItAndCanCancelIt()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory);
        Assert.True(queue.TryEnqueue(Item("q1")));
        await gate.EnteredAsync();
        using var stop = new CancellationTokenSource();

        var first = queue.DrainAsync(CancellationToken.None);
        var second = queue.DrainAsync(stop.Token);
        Assert.False(second.IsCompleted);
        await stop.CancelAsync();
        await Task.WhenAll(first, second).WaitAsync(Guard, Ct);

        Assert.True(gate.SawCancellation);
    }

    [Fact]
    public async Task NoReaderStartsBeforeTheFirstItem()
    {
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory);

        // With no reader to wait for, the drain has nothing pending and completes on the spot.
        var drain = queue.DrainAsync(Ct);

        Assert.True(drain.IsCompletedSuccessfully);
        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task TheReaderDoesNotRunInTheFirstCallersContext()
    {
        var ambient = new AsyncLocal<string?> { Value = "first caller" };
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new FakeReasonletMemory
        {
            RememberHandler = (_, _) =>
            {
                seen.TrySetResult(ambient.Value);
                return Task.FromResult(CaptureTestData.Receipt());
            },
        };
        await using var queue = Queue(memory);

        Assert.True(queue.TryEnqueue(Item("q1")));

        Assert.Null(await seen.Task.WaitAsync(Guard, Ct));
    }

    [Fact]
    public async Task AFirstEnqueueWithFlowAlreadySuppressedStartsTheReader()
    {
        var gate = new Gate();
        gate.Open();
        await using var queue = Queue(gate.Memory);

        using (ExecutionContext.SuppressFlow())
        {
            Assert.True(queue.TryEnqueue(Item("q1")));
        }

        Assert.Equal("q1", (await gate.EnteredAsync()).Question);
    }

    [Fact]
    public async Task TryCaptureDecidesPendingAtHandOff()
    {
        // An outer function invocation layer marks the call informational after the hand-off, while the run may still wait
        // in the queue; the hand-off keeps what was true then.
        var call = new FunctionCallContent("c1", "orders.get");
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory);
        using var runs = new RunCounter();

        Assert.True(queue.TryCapture(Run([new(ChatRole.User, CaptureTestData.Question), new(ChatRole.Assistant, [call])])));
        call.InformationalOnly = true;
        await queue.DrainAsync(Ct);

        Assert.Equal(["skipped/pending_tool_calls"], runs.Seen);
        Assert.Empty(memory.Captures);
    }

    [Fact]
    public async Task TryCaptureCopiesTheMessagesAtHandOff()
    {
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory);
        var messages = new List<ChatMessage>(Messages("q1"));

        Assert.True(queue.TryCapture(Run(messages)));
        messages.Clear();
        messages.Add(new ChatMessage(ChatRole.User, "other"));
        await queue.DrainAsync(Ct);

        Assert.Equal("q1", Assert.Single(memory.Captures).Question);
    }

    [Fact]
    public async Task TryCaptureOnAFullQueueReturnsFalseAndCounts()
    {
        var gate = new Gate();
        await using var queue = Queue(gate.Memory, capacity: 1);
        Assert.True(queue.TryCapture(Run(Messages("q1"))));
        await gate.EnteredAsync();
        Assert.True(queue.TryCapture(Run(Messages("q2"))));
        using var telemetry = new TelemetryCapture();

        Assert.False(queue.TryCapture(Run(Messages("q3"))));

        Assert.Equal([CaptureTelemetry.DroppedFullOutcome], Outcomes(telemetry));
        gate.Open();
    }

    [Fact]
    public async Task TryCaptureAfterDisposeReturnsFalse()
    {
        var queue = Queue(CaptureTestData.Memory());
        await queue.DisposeAsync();
        using var telemetry = new TelemetryCapture();

        Assert.False(queue.TryCapture(Run(Messages("q1"))));

        Assert.Equal([CaptureTelemetry.DroppedShutdownOutcome], Outcomes(telemetry));
    }

    [Fact]
    public async Task RunSourceReachesProvenance()
    {
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory, source: "from-options");

        Assert.True(queue.TryCapture(Run(Messages("q1")) with { Source = "x" }));
        Assert.True(queue.TryCapture(Run(Messages("q2"))));
        await queue.DrainAsync(Ct);

        Assert.Equal(["x", "from-options"], memory.Captures.Select(capture => capture.Provenance.Source));
    }

    [Fact]
    public async Task TryCaptureCarriesTheRunsFieldsToTheCapture()
    {
        var memory = CaptureTestData.Memory();
        await using var queue = Queue(memory);
        var run = Run(Messages("q1")) with { SubjectId = "s", RunId = "r", Duration = TimeSpan.FromSeconds(2), FinishReason = ChatFinishReason.Length };

        Assert.True(queue.TryCapture(run));
        await queue.DrainAsync(Ct);

        var capture = Assert.Single(memory.Captures);
        Assert.Equal("s", capture.Provenance.SubjectId);
        Assert.Equal("r", capture.Provenance.RunId);
        Assert.Equal(TimeSpan.FromSeconds(2), capture.Outcome.Duration);
        Assert.False(capture.Outcome.Success);
    }

    [Fact]
    public async Task TryCaptureRejectsANullRun()
    {
        await using var queue = Queue(CaptureTestData.Memory());

        Assert.Throws<ArgumentNullException>(() => queue.TryCapture(null!));
    }

    [Fact]
    public void ARunRejectsNullMessages() =>
        Assert.Throws<ArgumentNullException>(() => new ReasonletCaptureRun(new ReasonletScope("scope"), null!));

    [Fact]
    public void ARunRejectsNullLookups() =>
        Assert.Throws<ArgumentNullException>(() => new ReasonletCaptureRun(new ReasonletScope("scope"), []) { Lookups = null! });

    [Fact]
    public async Task QueueIsTheSinkIntegrationsSee()
    {
        await using var queue = Queue(CaptureTestData.Memory());

        Assert.IsAssignableFrom<IReasonletCaptureSink>(queue);
    }

    private static ReasonletCaptureRun Run(IReadOnlyList<ChatMessage> messages) => new(new ReasonletScope("scope"), messages);

    private static ChatMessage[] Messages(string question) => [new ChatMessage(ChatRole.User, question), .. CaptureTestData.Refund().Skip(1)];

    private static ReasonletCaptureQueue Queue(FakeReasonletMemory memory, int capacity = 256, TimeSpan? drainTimeout = null, string? source = null)
    {
        var options = new ReasonletCaptureOptions
        {
            Capacity = capacity,
            DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30),
            Source = source ?? new ReasonletCaptureOptions().Source,
        };

        return new ReasonletCaptureQueue(new ReasonletCaptureProcessor(memory), options);
    }

    /// <summary>The refund item, asked as <paramref name="question"/> so the order of the captures shows.</summary>
    private static ReasonletCaptureItem Item(string question) =>
        CaptureTestData.Item([new ChatMessage(ChatRole.User, question), .. CaptureTestData.Refund().Skip(1)]);

    private static string[] Outcomes(TelemetryCapture telemetry) =>
        [.. telemetry.Of("mnemosyne.capture.queue").Select(count => (string)count.Tag("mnemosyne.outcome")!)];

    /// <summary>
    /// A memory whose every remember reports that it has started and then waits until the test opens the gate. By default the
    /// wait ends on the call's token too, as a real store's would.
    /// </summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<ReasonletCapture> _entered = Channel.CreateUnbounded<ReasonletCapture>();
        private volatile bool _sawCancellation;

        public Gate(bool honourCancellation = true) => Memory = new FakeReasonletMemory
        {
            RememberHandler = async (capture, ct) =>
            {
                _entered.Writer.TryWrite(capture);
                try
                {
                    await (honourCancellation ? _open.Task.WaitAsync(ct) : _open.Task);
                }
                catch (OperationCanceledException)
                {
                    _sawCancellation = true;
                    throw;
                }

                return CaptureTestData.Receipt();
            },
        };

        public FakeReasonletMemory Memory { get; }

        /// <summary>Gets whether a remember ended on its token.</summary>
        public bool SawCancellation => _sawCancellation;

        public void Open() => _open.TrySetResult();

        /// <summary>Waits until the next remember has started, and returns its capture.</summary>
        public async Task<ReasonletCapture> EnteredAsync() => await _entered.Reader.ReadAsync(Ct).AsTask().WaitAsync(Guard, Ct);
    }
}
