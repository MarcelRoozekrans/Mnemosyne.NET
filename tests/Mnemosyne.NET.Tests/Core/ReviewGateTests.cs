using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

public sealed class ReviewGateTests
{
    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private ReasonletMemory Memory(MnemosyneOptions? options = null) =>
        new(_store, _selector, _generator, null, null, options ?? new MnemosyneOptions());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RememberCopiesRequireReviewOntoTheEntry(bool requireReview)
    {
        var result = await Memory(new MnemosyneOptions { RequireReview = requireReview })
            .RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(requireReview, _store.Remembered!.ReviewRequired);
    }

    [Fact]
    public async Task ApproveRejectAndListDelegate()
    {
        var scope = new ReasonletScope("tenant");
        var id = ReasonletId.New();
        var after = ReasonletId.New();
        using var cts = new CancellationTokenSource();
        var denied = new MnemosyneError(MnemosyneErrorCode.Conflict, "approve said no");
        var refused = new MnemosyneError(MnemosyneErrorCode.NotFound, "reject said no");
        var page = new List<ReasonletReview>
        {
            new(TestData.Definition(), new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch)),
        };
        _store.ApproveResult = denied;
        _store.RejectResult = refused;
        _store.ListPendingResult = Result<IReadOnlyList<ReasonletReview>, MnemosyneError>.Success(page);
        var memory = Memory();

        var approved = await memory.ApproveAsync(scope, id, cts.Token);
        var rejected = await memory.RejectAsync(scope, id, cts.Token);
        var listed = await memory.ListPendingAsync(scope, 100, after, cts.Token);

        Assert.Equal(denied, approved.Error);
        Assert.Equal(refused, rejected.Error);
        Assert.Same(page, listed.Value);
        Assert.Equal((scope, id, cts.Token), _store.Approved!.Value);
        Assert.Equal((scope, id, cts.Token), _store.Rejected!.Value);
        Assert.Equal((scope, 100, (ReasonletId?)after, cts.Token), _store.Listed!.Value);
    }

    [Fact]
    public async Task ApproveAndRejectPassSuccessThrough()
    {
        var memory = Memory();

        Assert.True((await memory.ApproveAsync(new ReasonletScope("t"), ReasonletId.New(), CancellationToken.None)).IsSuccess);
        Assert.True((await memory.RejectAsync(new ReasonletScope("t"), ReasonletId.New(), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ReviewCallsValidateBeforeTheStore()
    {
        var memory = Memory();
        var scope = new ReasonletScope("tenant");
        var id = ReasonletId.New();
        var ct = CancellationToken.None;

        var results = new List<MnemosyneError>
        {
            (await memory.ApproveAsync(default, id, ct)).Error,
            (await memory.ApproveAsync(scope, default, ct)).Error,
            (await memory.RejectAsync(default, id, ct)).Error,
            (await memory.RejectAsync(scope, default, ct)).Error,
            (await memory.ListPendingAsync(default, 10, null, ct)).Error,
            (await memory.ListPendingAsync(scope, 0, null, ct)).Error,
            (await memory.ListPendingAsync(scope, 101, null, ct)).Error,
            (await memory.ListPendingAsync(scope, 10, default(ReasonletId), ct)).Error,
        };

        Assert.All(results, error => Assert.Equal(MnemosyneErrorCode.Validation, error.Code));
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ListPendingPinsTheLimitText(int limit)
    {
        var result = await Memory().ListPendingAsync(new ReasonletScope("tenant"), limit, null, CancellationToken.None);

        Assert.Equal("limit: must be from 1 to 100", result.Error.Message);
    }

    [Fact]
    public async Task ListPendingNamesTheDefaultAfterPath()
    {
        var result = await Memory().ListPendingAsync(new ReasonletScope("tenant"), 10, default(ReasonletId), CancellationToken.None);

        Assert.Contains("after", result.Error.Message);
    }

    [Fact]
    public async Task ListPendingAcceptsTheLimitBounds()
    {
        var memory = Memory();

        Assert.True((await memory.ListPendingAsync(new ReasonletScope("t"), 1, null, CancellationToken.None)).IsSuccess);
        Assert.True((await memory.ListPendingAsync(new ReasonletScope("t"), 100, null, CancellationToken.None)).IsSuccess);
        Assert.Equal(2, _store.Calls);
    }
}
