using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Result = ZeroAlloc.Results.Result<Mnemosyne.NET.Abstractions.ReasonletReceipt, Mnemosyne.NET.Abstractions.MnemosyneError>;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>Builders for the capture processor's tests: the AOT smoke's refund transcript, items and receipts.</summary>
internal static class CaptureTestData
{
    public const string Question = "Refund order 7";

    public static ChatMessage[] Refund() =>
    [
        new(ChatRole.User, Question),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "orders.get", new Dictionary<string, object?> { ["id"] = 7 })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
        new(ChatRole.Assistant, [new FunctionCallContent("c2", "payments.refund", new Dictionary<string, object?> { ["orderId"] = 7, ["amount"] = 20 })]),
        new(ChatRole.Tool, [new FunctionResultContent("c2", "refunded")]),
        new(ChatRole.Assistant, "Refunded."),
    ];

    /// <summary>A run of <c>find_procedure</c> ahead of the refund steps, answered by <c>l1</c>.</summary>
    public static ChatMessage[] RefundWithLookup() =>
    [
        new(ChatRole.User, Question),
        new(ChatRole.Assistant, [new FunctionCallContent("l1", ReasonletTools.FindProcedureName, new Dictionary<string, object?> { ["question"] = "q", ["top"] = 3 })]),
        new(ChatRole.Tool, [new FunctionResultContent("l1", "[]")]),
        .. Refund().Skip(1),
    ];

    /// <summary>An item as the middleware hands it off, with the pending call decided from its messages.</summary>
    public static ReasonletCaptureItem Item(IReadOnlyList<ChatMessage>? messages = null, string source = "test.source")
    {
        messages ??= Refund();
        return new(
            new ReasonletScope("scope"),
            "subject-1",
            messages,
            null,
            [],
            TimeSpan.FromMilliseconds(1500),
            "resp-1",
            ChatFinishReason.Stop,
            ReasonletCaptureProcessor.EndsWithPendingCall(messages),
            source);
    }

    public static ReasonletMatch Match(ReasonletGraph? graph = null) => new() { Reasonlet = TestData.Definition() with { Id = ReasonletId.New(), Graph = graph ?? TestData.Graph() }, Score = default, Evidence = default };

    public static ReasonletGraph GraphOf(IEnumerable<ChatMessage> messages) => ReasonletTranscript.ToGraph(messages).Value;

    public static Result Receipt(ReasonletCaptureKind kind = ReasonletCaptureKind.NewReasonlet) => Result.Success(
        new ReasonletReceipt(
            ReasonletId.New(), "1.0.0", new ExecutionId(Guid.NewGuid()), kind, ReasonletStatus.Active, ReasonletVerdictSource.Agent, true));

    public static FakeReasonletMemory Memory(ReasonletCaptureKind kind = ReasonletCaptureKind.NewReasonlet)
    {
        var memory = new FakeReasonletMemory();
        memory.RememberHandler = (_, _) => Task.FromResult(Receipt(kind));
        return memory;
    }
}
