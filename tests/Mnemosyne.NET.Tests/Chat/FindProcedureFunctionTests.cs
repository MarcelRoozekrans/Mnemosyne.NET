using System.Text.Json;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class FindProcedureFunctionTests
{
    private const string ExpectedSchema =
        """{"type":"object","properties":{"question":{"type":"string","description":"The question or task to find a procedure for."},"top":{"type":"integer","minimum":1,"maximum":5,"description":"How many procedures to return. Defaults to 3."}},"required":["question"],"additionalProperties":false}""";

    private static readonly ReasonletScope Scope = new("s");

    [Fact]
    public void NameDescriptionAndSchemaAreFixed()
    {
        var function = ReasonletTools.CreateFindProcedure(new FakeReasonletMemory(), Scope);

        Assert.Equal("find_procedure", function.Name);
        Assert.Equal("find_procedure", ReasonletTools.FindProcedureName);
        Assert.Equal(
            "Looks up procedures that worked before for a similar question. Each has the tools it called, in order, with their argument names.",
            function.Description);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(ExpectedSchema).RootElement, function.JsonSchema));
    }

    [Fact]
    public async Task RecallsInTheBoundScopeWithTop()
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", Json("\"refund\"")), ("top", Json("2"))), CancellationToken.None);

        var query = Assert.Single(memory.Queries);
        Assert.Equal(Scope, query.Scope);
        Assert.Equal("refund", query.Question);
        Assert.Equal(2, query.Top);
    }

    [Fact]
    public async Task ClrValuesAreAccepted()
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", "refund"), ("top", 4)), CancellationToken.None);

        var query = Assert.Single(memory.Queries);
        Assert.Equal("refund", query.Question);
        Assert.Equal(4, query.Top);
    }

    [Fact]
    public async Task TopDefaultsToThree()
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        Assert.Equal(3, Assert.Single(memory.Queries).Top);
    }

    [Fact]
    public async Task ReturnsMatchesAsJson()
    {
        var memory = new FakeReasonletMemory();
        var match = Match();
        memory.Returns(match);
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        var result = await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        var item = Assert.Single(Assert.IsType<JsonElement>(result).EnumerateArray());
        Assert.Equal(match.Reasonlet.Id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("refund-order", item.GetProperty("name").GetString());
        Assert.False(item.TryGetProperty("intent", out _));
        Assert.Equal("1", item.GetProperty("version").GetString());
        Assert.Equal(0.82, item.GetProperty("similarity").GetDouble());
        Assert.Equal(0.75, item.GetProperty("successRate").GetDouble());
        Assert.Equal(11, item.GetProperty("runs").GetInt32());
        var steps = item.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Equal("find_order", steps[0].GetProperty("tool").GetString());
        Assert.Equal(["customerId", "status"], steps[0].GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("ping", steps[1].GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.Array, steps[1].GetProperty("arguments").ValueKind);
        Assert.Equal(0, steps[1].GetProperty("arguments").GetArrayLength());
    }

    [Fact]
    public async Task NoMatchesReturnsAnEmptyArray()
    {
        var function = ReasonletTools.CreateFindProcedure(new FakeReasonletMemory(), Scope);

        var result = await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        Assert.Equal(0, Assert.IsType<JsonElement>(result).GetArrayLength());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("refund", "0")]
    [InlineData("refund", "6")]
    [InlineData("refund", "\"x\"")]
    [InlineData("refund", "1.5")]
    public async Task InvalidArgumentsDoNotRecall(string? question, string? top)
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);
        var arguments = new AIFunctionArguments();
        if (question is not null)
        {
            arguments["question"] = Json("\"" + question + "\"");
        }

        if (top is not null)
        {
            arguments["top"] = Json(top);
        }

        var result = await function.InvokeAsync(arguments, CancellationToken.None);

        Assert.Equal("""{"error":"invalid_arguments"}""", Assert.IsType<JsonElement>(result).GetRawText());
        Assert.Empty(memory.Queries);
    }

    [Theory]
    [MemberData(nameof(WrongClrArguments))]
    public async Task WrongClrTypesAreInvalid(object? question, object? top)
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        var result = await function.InvokeAsync(Args(("question", question), ("top", top)), CancellationToken.None);

        Assert.Equal("""{"error":"invalid_arguments"}""", Assert.IsType<JsonElement>(result).GetRawText());
        Assert.Empty(memory.Queries);
    }

    [Fact]
    public async Task LongTopIsAccepted()
    {
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", "refund"), ("top", 5L)), CancellationToken.None);

        Assert.Equal(5, Assert.Single(memory.Queries).Top);
    }

    public static TheoryData<object?, object?> WrongClrArguments() => new()
    {
        { 5, 2 },
        { null, 2 },
        { "refund", "x" },
        { "refund", 6L },

        // A long cast to int keeps only its low 32 bits, which for this value are 1; the lower bound keeps it out.
        { "refund", -4294967295L },
        { "refund", 2.5 },
        { "refund", 0 },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANullTopDefaultsToThree(bool json)
    {
        object? top = json ? Json("null") : null;
        var memory = new FakeReasonletMemory();
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", "refund"), ("top", top)), CancellationToken.None);

        Assert.Equal(3, Assert.Single(memory.Queries).Top);
    }

    [Fact]
    public async Task ErrorResultReturnsUnavailable()
    {
        var memory = new FakeReasonletMemory();
        memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, "secret store detail"));
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        var result = await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        var text = Assert.IsType<JsonElement>(result).GetRawText();
        Assert.Equal("""{"error":"unavailable"}""", text);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionReturnsUnavailable()
    {
        var memory = new FakeReasonletMemory();
        memory.Throws(new InvalidOperationException("secret failure"));
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        var result = await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        var text = Assert.IsType<JsonElement>(result).GetRawText();
        Assert.Equal("""{"error":"unavailable"}""", text);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var memory = new FakeReasonletMemory();
        memory.Throws(new OperationCanceledException(cts.Token));
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await function.InvokeAsync(Args(("question", "refund")), cts.Token));
    }

    [Fact]
    public async Task ACancellationThatIsNotTheCallersReturnsUnavailable()
    {
        var memory = new FakeReasonletMemory();
        memory.Throws(new TaskCanceledException("store timeout"));
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        var result = await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        Assert.Equal("""{"error":"unavailable"}""", Assert.IsType<JsonElement>(result).GetRawText());
    }

    [Fact]
    public async Task LookupsAreRecordedInOrderAndUnderParallelCalls()
    {
        var memory = new FakeReasonletMemory();
        var first = Match();
        memory.RecallHandler = (query, _) => Task.FromResult(
            ZeroAlloc.Results.Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
                query.Question == "one" ? [first] : []));
        var function = new FindProcedureFunction(memory, Scope);

        await function.InvokeAsync(Args(("question", "one")), CancellationToken.None);
        await function.InvokeAsync(Args(("question", "two")), CancellationToken.None);

        Assert.Equal(2, function.Lookups.Count);
        Assert.Same(first, Assert.Single(function.Lookups[0]));
        Assert.Empty(function.Lookups[1]);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            await function.InvokeAsync(Args(("question", $"q{i}")), CancellationToken.None))));

        Assert.Equal(22, function.Lookups.Count);
    }

    [Fact]
    public async Task HostCreatedToolReportsItsLookups()
    {
        var memory = new FakeReasonletMemory();
        var first = Match();
        memory.RecallHandler = (query, _) => Task.FromResult(
            ZeroAlloc.Results.Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
                query.Question == "one" ? [first] : []));
        var function = ReasonletTools.CreateFindProcedure(memory, Scope);

        await function.InvokeAsync(Args(("question", "one")), CancellationToken.None);
        var snapshot = function.Lookups;
        await function.InvokeAsync(Args(("question", "two")), CancellationToken.None);

        Assert.Equal(2, function.Lookups.Count);
        Assert.Same(first, Assert.Single(function.Lookups[0]));
        Assert.Empty(function.Lookups[1]);
        Assert.Single(snapshot);
    }

    [Fact]
    public void CreateFindProcedureRejectsANullMemory() =>
        Assert.Throws<ArgumentNullException>("memory", () => ReasonletTools.CreateFindProcedure(null!, Scope));

    [Fact]
    public async Task FailedRecallsAreNotRecorded()
    {
        var memory = new FakeReasonletMemory();
        memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, "x"));
        var function = new FindProcedureFunction(memory, Scope);

        await function.InvokeAsync(Args(("question", "refund")), CancellationToken.None);

        Assert.Empty(function.Lookups);
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] values)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in values)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static ReasonletMatch Match()
    {
        var nodes = new[]
        {
            Node("a", "find_order", """["customerId","status"]"""),
            Node("b", "ping", "[]"),
        };
        var graph = new ReasonletGraph
        {
            Nodes = nodes,
            Edges = [new ReasonletEdge { From = "a", To = "b" }],
            SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
        };
        return new ReasonletMatch
        {
            Reasonlet = new ReasonletDefinition
            {
                Id = ReasonletId.New(),
                Scope = Scope,
                Name = "refund-order",
                Intent = "refund an order",
                Version = "1",
                Graph = graph,
                Provenance = new ReasonletProvenance { Source = "test" },
                Status = ReasonletStatus.Active,
                CreatedAt = DateTimeOffset.UnixEpoch,
            },
            Score = new ReasonletScore(Similarity: 0.82, SuccessRate: 0.75, Freshness: 1, Total: 0.8),
            Evidence = new ReasonletEvidence { Runs = 11, JudgedRuns = 11, UsesParentPrior = false },
            LineageId = ReasonletId.New(),
        };
    }

    private static ReasonletNode Node(string id, string tool, string arguments) =>
        new()
        {
            Id = id,
            Type = "tool",
            Tool = tool,
            Properties = new Dictionary<string, JsonElement> { ["mnemosyne.arguments"] = Json(arguments) },
        };
}
