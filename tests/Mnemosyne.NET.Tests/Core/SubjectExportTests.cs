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
    public async Task ListSubjectExecutionsDelegatesToTheStoreWithTheCallersArguments()
    {
        var cursor = ExecutionId.New();
        using var cts = new CancellationTokenSource();
        var failure = new MnemosyneError(MnemosyneErrorCode.Unavailable, "store down");
        _store.ListSubjectExecutionsResult = Result<IReadOnlyList<ReasonletExecutionRecord>, MnemosyneError>.Failure(failure);

        var result = await Memory().ListSubjectExecutionsAsync(Scope, "subject-1", 25, cursor, cts.Token);

        Assert.Equal(failure, result.Error);
        Assert.Equal((Scope, "subject-1", 25, (ExecutionId?)cursor, cts.Token), _store.ListedSubjectExecutions);
    }

    [Fact]
    public async Task RejectsInvalidArgumentsWithTheExactMessageWithoutCallingTheStore()
    {
        var memory = Memory();
        var ct = CancellationToken.None;
        const string LimitText = "limit: must be from 1 to 100";
        const string BlankText = "subjectId: must not be blank";
        (string Case, ReasonletScope Scope, string? Subject, int Limit, bool DefaultCursor, string Expected)[] cases =
        [
            ("default scope", default, "s", 10, false, "scope.Value: must not be blank"),
            ("null subject", Scope, null, 10, false, BlankText),
            ("empty subject", Scope, "", 10, false, BlankText),
            ("blank subject", Scope, "  ", 10, false, BlankText),
            ("limit zero", Scope, "s", 0, false, LimitText),
            ("limit above the maximum", Scope, "s", 101, false, LimitText),
            ("default cursor", Scope, "s", 10, true, "after: must not be the default id"),
        ];

        foreach (var (name, scope, subject, limit, defaultCursor, expected) in cases)
        {
            var reasonlets = await memory.ListSubjectReasonletsAsync(scope, subject!, limit, defaultCursor ? default(ReasonletId) : null, ct);
            var runs = await memory.ListSubjectExecutionsAsync(scope, subject!, limit, defaultCursor ? default(ExecutionId) : null, ct);

            Assert.True(reasonlets.IsFailure, name);
            Assert.True(runs.IsFailure, name);
            Assert.Equal(MnemosyneErrorCode.Validation, reasonlets.Error.Code);
            Assert.Equal(MnemosyneErrorCode.Validation, runs.Error.Code);
            Assert.Equal(expected, reasonlets.Error.Message);
            Assert.Equal(expected, runs.Error.Message);
        }

        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task AcceptsTheLimitBounds()
    {
        var memory = Memory();
        var ct = CancellationToken.None;

        Assert.True((await memory.ListSubjectReasonletsAsync(Scope, "s", 1, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectReasonletsAsync(Scope, "s", 100, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectExecutionsAsync(Scope, "s", 1, null, ct)).IsSuccess);
        Assert.True((await memory.ListSubjectExecutionsAsync(Scope, "s", 100, null, ct)).IsSuccess);
        Assert.Equal(4, _store.Calls);
    }
}
