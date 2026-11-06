using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

public sealed class SubjectExportTests
{
    private static readonly ReasonletScope Scope = new("tenant");

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private ReasonletMemory Memory() => new(_store, _selector, _generator, null, null, new MnemosyneOptions());

    [Fact]
    public async Task ListSubjectReasonletsDelegatesToTheStoreWithTheCallersArguments()
    {
        var cursor = ReasonletId.New();
        using var cts = new CancellationTokenSource();
        var record = new ReasonletRecord
        {
            Definition = TestData.Definition(),
            Metrics = new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch),
        };
        _store.ListSubjectReasonletsResult = Result<IReadOnlyList<ReasonletRecord>, MnemosyneError>.Success([record]);

        var result = await Memory().ListSubjectReasonletsAsync(Scope, "subject-1", 25, cursor, cts.Token);

        Assert.Same(record, Assert.Single(result.Value));
        Assert.Equal((Scope, "subject-1", 25, (ReasonletId?)cursor, cts.Token), _store.ListedSubjectReasonlets);
    }

    [Fact]
    public async Task ListSubjectRunsDelegatesToTheStoreWithTheCallersArguments()
    {
        var cursor = ExecutionId.New();
        using var cts = new CancellationTokenSource();
        var failure = new MnemosyneError(MnemosyneErrorCode.Unavailable, "store down");
        _store.ListSubjectRunsResult = Result<IReadOnlyList<ReasonletExecutionRecord>, MnemosyneError>.Failure(failure);

        var result = await Memory().ListSubjectRunsAsync(Scope, "subject-1", 25, cursor, cts.Token);

        Assert.Equal(failure, result.Error);
        Assert.Equal((Scope, "subject-1", 25, (ExecutionId?)cursor, cts.Token), _store.ListedSubjectRuns);
    }

    [Fact]
    public async Task RejectsInvalidArgumentsWithoutCallingTheStore()
    {
        var memory = Memory();
        var ct = CancellationToken.None;

        var results = new List<MnemosyneError>
        {
            (await memory.ListSubjectReasonletsAsync(default, "s", 10, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, null!, 10, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, "", 10, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, "  ", 10, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, "s", 0, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, "s", 101, null, ct)).Error,
            (await memory.ListSubjectReasonletsAsync(Scope, "s", 10, default(ReasonletId), ct)).Error,
            (await memory.ListSubjectRunsAsync(default, "s", 10, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, null!, 10, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, "", 10, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, "  ", 10, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, "s", 0, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, "s", 101, null, ct)).Error,
            (await memory.ListSubjectRunsAsync(Scope, "s", 10, default(ExecutionId), ct)).Error,
        };

        Assert.All(results, error => Assert.Equal(MnemosyneErrorCode.Validation, error.Code));
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task PinsTheLimitText(int limit)
    {
        var reasonlets = await Memory().ListSubjectReasonletsAsync(Scope, "s", limit, null, CancellationToken.None);
        var runs = await Memory().ListSubjectRunsAsync(Scope, "s", limit, null, CancellationToken.None);

        Assert.Equal("limit: must be from 1 to 100", reasonlets.Error.Message);
        Assert.Equal("limit: must be from 1 to 100", runs.Error.Message);
    }

    [Fact]
    public async Task PinsTheSubjectAndCursorTexts()
    {
        var memory = Memory();
        var ct = CancellationToken.None;

        Assert.Equal("subjectId: must not be blank", (await memory.ListSubjectRunsAsync(Scope, " ", 10, null, ct)).Error.Message);
        Assert.Equal("after: must not be the default id", (await memory.ListSubjectRunsAsync(Scope, "s", 10, default(ExecutionId), ct)).Error.Message);
    }

    [Fact]
    public async Task AcceptsTheLimitBounds()
    {
        var memory = Memory();
        var ct = CancellationToken.None;

        Assert.True((await memory.ListSubjectReasonletsAsync(Scope, "s", 1, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectReasonletsAsync(Scope, "s", 100, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectRunsAsync(Scope, "s", 1, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectRunsAsync(Scope, "s", 100, null, ct)).IsSuccess);
        Assert.Equal(4, _store.Calls);
    }
}
