using System.Buffers;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class RememberTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutAParentANewReasonletIsStoredAtVersion1_0WithOneExecution()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var entry = Entries.Entry(scope);

        var result = await store.RememberAsync(entry, Ct);

        Assert.True(result.IsSuccess);
        var receipt = result.Value;
        Assert.Equal(ReasonletCaptureKind.NewReasonlet, receipt.Kind);
        Assert.Equal("1.0", receipt.Version);
        Assert.Equal(ReasonletStatus.Active, receipt.Status);
        Assert.False(receipt.Id.IsDefault);
        Assert.False(receipt.Execution.IsDefault);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
        Assert.Null(await ParentOfAsync(receipt.Id));
        await AssertSingleExecutionAsync(receipt, entry.Capture);
    }

    [Fact]
    public async Task TheStoredGraphIsTheExactSerializedTextWithItsHash()
    {
        var store = await MigratedStoreAsync();
        var entry = Entries.Entry(PgVectorFixture.NewScope());

        var receipt = (await store.RememberAsync(entry, Ct)).Value;

        Assert.Equal(
            ReasonletGraphJson.Serialize(entry.Capture.Graph),
            await Entries.ScalarAsync<string>(fixture.DataSource, "SELECT graph FROM mnemosyne.reasonlets WHERE id = $1", receipt.Id.Value));
        Assert.Equal(
            entry.Capture.Graph.ComputeContentHash().Value,
            await Entries.ScalarAsync<string>(fixture.DataSource, "SELECT content_hash FROM mnemosyne.reasonlets WHERE id = $1", receipt.Id.Value));
    }

    [Fact]
    public async Task AParentWithAnEqualGraphRecordsOnlyAnExecutionOnTheParent()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        var entry = Entries.Entry(
            scope,
            parentId: parent.Id,
            outcome: new ReasonletOutcome(false, TimeSpan.FromSeconds(3), null),
            provenance: new ReasonletProvenance { Source = "second" });

        var result = await store.RememberAsync(entry, Ct);

        Assert.True(result.IsSuccess);
        var receipt = result.Value;
        Assert.Equal(ReasonletCaptureKind.ExecutionOnly, receipt.Kind);
        Assert.Equal(parent.Id, receipt.Id);
        Assert.Equal("1.0", receipt.Version);
        Assert.Equal(ReasonletStatus.Active, receipt.Status);
        Assert.NotEqual(parent.Execution, receipt.Execution);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", parent.Id.Value));
        await AssertSingleExecutionAsync(receipt, entry.Capture);
    }

    [Fact]
    public async Task ExecutionOnlyReportsTheParentsStatus()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET status = $1 WHERE id = $2", (short)ReasonletStatus.PendingReview, parent.Id.Value);

        var receipt = (await store.RememberAsync(Entries.Entry(scope, parentId: parent.Id), Ct)).Value;

        Assert.Equal(ReasonletCaptureKind.ExecutionOnly, receipt.Kind);
        Assert.Equal(ReasonletStatus.PendingReview, receipt.Status);
    }

    [Fact]
    public async Task ADifferentGraphCreatesVersionsThatShareOneSequenceAcrossTheLineage()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;

        var firstEntry = Entries.Entry(scope, Entries.Graph("first"), root.Id);
        var first = (await store.RememberAsync(firstEntry, Ct)).Value;
        var second = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("second"), root.Id), Ct)).Value;
        var grandchild = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("grandchild"), first.Id), Ct)).Value;

        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.1", ReasonletStatus.Active), (first.Kind, first.Version, first.Status));
        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.2"), (second.Kind, second.Version));
        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.3"), (grandchild.Kind, grandchild.Version));
        Assert.Equal(4, new[] { root.Id, first.Id, second.Id, grandchild.Id }.Distinct().Count());
        Assert.Equal(root.Id.Value, await ParentOfAsync(first.Id));
        Assert.Equal(root.Id.Value, await ParentOfAsync(second.Id));
        Assert.Equal(first.Id.Value, await ParentOfAsync(grandchild.Id));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", root.Id.Value));
        await AssertSingleExecutionAsync(first, firstEntry.Capture);
    }

    [Fact]
    public async Task AGraphReadBackAndSentAgainWithItsParentRecordsOnlyAnExecution()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        var recalled = (await store.GetAsync(scope, parent.Id, Ct)).Value;

        var receipt = (await store.RememberAsync(Entries.Entry(scope, recalled.Graph, parent.Id), Ct)).Value;

        Assert.Equal(ReasonletCaptureKind.ExecutionOnly, receipt.Kind);
        Assert.Equal(parent.Id, receipt.Id);
    }

    [Fact]
    public async Task AMissingParentIsNotFoundAndStoresNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var result = await store.RememberAsync(Entries.Entry(scope, parentId: ReasonletId.New()), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task AParentInAnotherScopeIsNotFoundAndStoresNothing()
    {
        var store = await MigratedStoreAsync();
        var other = (await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope()), Ct)).Value;
        var scope = PgVectorFixture.NewScope();

        var equal = await store.RememberAsync(Entries.Entry(scope, parentId: other.Id), Ct);
        var different = await store.RememberAsync(Entries.Entry(scope, Entries.Graph("other"), other.Id), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, equal.Error.Code);
        Assert.Equal(MnemosyneErrorCode.NotFound, different.Error.Code);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", other.Id.Value));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task TwentyParallelChildrenOfOneParentGetVersions1_1To1_20WithoutGaps()
    {
        // The fixture's data source keeps Npgsql's default pool of 100 connections, enough for 20 callers that each
        // hold a connection while they wait for the lineage top's row lock.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(
            i => Task.Run(() => store.RememberAsync(Entries.Entry(scope, Entries.Graph($"child-{i}"), root.Id), Ct).AsTask(), Ct)));

        Assert.All(results, r => Assert.Equal(ReasonletCaptureKind.NewVersion, r.Value.Kind));
        Assert.Equal(
            Enumerable.Range(1, 20).Select(minor => $"1.{minor}").Order(StringComparer.Ordinal),
            results.Select(r => r.Value.Version).Order(StringComparer.Ordinal));
        Assert.Equal(20, results.Select(r => r.Value.Id).Distinct().Count());
    }

    [Fact]
    public async Task ParallelChildrenOfTwoParentsInOneLineageShareOneSequenceWithoutGaps()
    {
        // Children of the root lock only the root; children of 1.1 lock 1.1 and then the root, the lineage top. Only
        // the top's lock serializes the two groups.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;
        var first = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("first"), root.Id), Ct)).Value;

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(
            i => Task.Run(
                () => store.RememberAsync(Entries.Entry(scope, Entries.Graph($"child-{i}"), i % 2 == 0 ? root.Id : first.Id), Ct).AsTask(),
                Ct)));

        Assert.Equal(
            Enumerable.Range(2, 20).Select(minor => $"1.{minor}").Order(StringComparer.Ordinal),
            results.Select(r => r.Value.Version).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ParallelChildrenOfParentsAtEveryDepthOfALineageGetDistinctVersions()
    {
        // Four parents at depths 0 to 3; each child locks its own parent and then the lineage top.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;
        var a = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("a"), root.Id), Ct)).Value;
        var b = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b"), a.Id), Ct)).Value;
        var c = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("c"), b.Id), Ct)).Value;
        ReasonletId[] parents = [root.Id, a.Id, b.Id, c.Id];

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(
            i => Task.Run(
                () => store.RememberAsync(Entries.Entry(scope, Entries.Graph($"child-{i}"), parents[i % 4]), Ct).AsTask(), Ct)));

        Assert.Equal(
            Enumerable.Range(4, 20).Select(minor => $"1.{minor}").Order(StringComparer.Ordinal),
            results.Select(r => r.Value.Version).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AfterTheRootIsErasedTheSurvivingSubtreeKeepsItsNumbering()
    {
        // R 1.0 <- P 1.1 <- G 1.2. Erasing R makes P a top, and P's lineage is P and everything under it, so G's 1.2
        // still counts and P's next child is 1.3, never a second 1.2.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("r")), Ct)).Value;
        var p = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("p"), r.Id), Ct)).Value;
        var g = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("g"), p.Id), Ct)).Value;
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlets WHERE id = $1", r.Id.Value);

        var next = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), p.Id), Ct)).Value;

        Assert.Equal(("1.1", "1.2"), (p.Version, g.Version));
        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.3"), (next.Kind, next.Version));
        Assert.Null(await ParentOfAsync(p.Id));
        Assert.Equal(["1.1", "1.2", "1.3"], await SubtreeVersionsAsync(p.Id));
    }

    [Fact(Timeout = 30_000)]
    public async Task ADeepLineageGetsItsNextVersionInLinearTime()
    {
        // A 4,000-deep chain. Walks that carry their path, or one lock statement per ancestor, take seconds here.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var leaf = await ChainAsync(scope, 4000);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var next = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), new ReasonletId(leaf)), Ct)).Value;
        started.Stop();

        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.4000"), (next.Kind, next.Version));
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TheAncestorsAreLockedBottomUpInOneStatement()
    {
        // P <- A1 <- A2 <- A3 <- T, P the deepest. Another transaction holds A2, so the store's lock statement takes A1
        // and waits for A2. While it waits, A1 is locked and A3 is not: the rows are locked in path order.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var t = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("t")), Ct)).Value;
        var a3 = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("a3"), t.Id), Ct)).Value;
        var a2 = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("a2"), a3.Id), Ct)).Value;
        var a1 = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("a1"), a2.Id), Ct)).Value;
        var p = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("p"), a1.Id), Ct)).Value;

        await using var holder = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await RunAsync(holder, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{a2.Id.Value}' FOR UPDATE");

        var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), p.Id), Ct).AsTask();
        await WaitForLockWaitAsync(holder);
        var a1Locked = await IsLockedAsync(a1.Id);
        var a3Locked = await IsLockedAsync(a3.Id);
        await transaction.CommitAsync(Ct);
        var result = await remember;

        Assert.True(a1Locked);
        Assert.False(a3Locked);
        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.5"), (result.Value.Kind, result.Value.Version));
    }

    [Fact]
    public async Task AnAppendToAParentThatARememberHoldsDoesNotWait()
    {
        // The store's remember of a child of P holds P and waits for T, which another transaction holds as a remember
        // would. Its FOR NO KEY UPDATE on P lets the append's FOR KEY SHARE through, so the append completes meanwhile.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var t = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("t")), Ct)).Value;
        var p = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("p"), t.Id), Ct)).Value;

        await using var holder = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await RunAsync(holder, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{t.Id.Value}' FOR NO KEY UPDATE");

        var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), p.Id), Ct).AsTask();
        await WaitForLockWaitAsync(holder);
        var append = store.AppendExecutionAsync(scope, p.Id, new ReasonletOutcome(true, TimeSpan.Zero, null), Entries.Provenance(), null, Ct).AsTask();
        var appended = await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        var rememberWaited = !remember.IsCompleted;
        await transaction.CommitAsync(Ct);
        var result = await remember;

        Assert.Same(append, appended);
        Assert.True((await append).IsSuccess);
        Assert.True(rememberWaited);
        Assert.Equal("1.2", result.Value.Version);
    }

    [Fact(Timeout = 30_000)]
    public async Task AParentCycleIsCorruptDataAndThrowsRatherThanLooping()
    {
        // The store never creates a cycle: a parent exists before its child, and parent_id only ever changes to NULL.
        // Hand-written SQL can, and an unguarded walk up would recurse forever.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var a = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("a")), Ct)).Value;
        var b = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b"), a.Id), Ct)).Value;
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET parent_id = $1 WHERE id = $2", b.Id.Value, a.Id.Value);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), b.Id), Ct));

        Assert.Contains("cycle", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task ATopErasedWhileTheStoreWaitsForItsLockIsAConflict()
    {
        // T 1.0 <- C 1.1 <- P 1.2. The store locks P and finds T as the top while the erase of T is still open; the
        // erase commits while the store waits for T's lock, so the lock finds no row. Counting T's subtree then would
        // miss C, so the store answers Conflict rather than guess.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var t = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("t")), Ct)).Value;
        var c = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("c"), t.Id), Ct)).Value;
        var p = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("p"), c.Id), Ct)).Value;

        await using var erase = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await erase.BeginTransactionAsync(Ct);
        await RunAsync(erase, $"DELETE FROM mnemosyne.reasonlets WHERE id = '{t.Id.Value}'");

        var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), p.Id), Ct).AsTask();
        await WaitForLockWaitAsync(erase);
        await transaction.CommitAsync(Ct);
        var result = await remember;

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(["1.1", "1.2"], await SubtreeVersionsAsync(c.Id));
    }

    [Fact]
    public async Task AMiddleAncestorErasedWhileTheStoreWaitsForItsLockIsAConflict()
    {
        // T 1.0 <- M 1.1 <- N 1.2 <- P 1.4, and T <- X 1.3. Erasing M cuts N from T, so T's subtree would be {T, X}
        // and a new child of P would repeat P's 1.4. The store locks the whole path P, N, M, T, so it waits for the
        // erase of M and then finds M gone.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var t = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("t")), Ct)).Value;
        var m = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("m"), t.Id), Ct)).Value;
        var n = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("n"), m.Id), Ct)).Value;
        var x = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("x"), t.Id), Ct)).Value;
        var p = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("p"), n.Id), Ct)).Value;
        Assert.Equal(["1.1", "1.2", "1.3", "1.4"], new[] { m.Version, n.Version, x.Version, p.Version });

        await using var erase = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await erase.BeginTransactionAsync(Ct);
        await RunAsync(erase, $"DELETE FROM mnemosyne.reasonlets WHERE id = '{m.Id.Value}'");

        var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("next"), p.Id), Ct).AsTask();
        await WaitForLockWaitAsync(erase);
        await transaction.CommitAsync(Ct);
        var result = await remember;

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(4L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE version = '1.4' AND scope = $1", scope.Value));
        Assert.Equal(["1.0", "1.3"], await SubtreeVersionsAsync(t.Id));
        Assert.Equal(["1.2", "1.4"], await SubtreeVersionsAsync(n.Id));
        Assert.Null(await ParentOfAsync(n.Id));
        Assert.Equal(n.Id.Value, await ParentOfAsync(p.Id));
        Assert.Equal(t.Id.Value, await ParentOfAsync(x.Id));
    }

    [Fact]
    public async Task TheTransactionIsReadCommittedWhateverTheServerDefault()
    {
        // At REPEATABLE READ a child that waited for the root's lock would read the lineage from its first snapshot,
        // miss the sibling committed meanwhile and take the same version. The server default must not leak in.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        await Entries.ExecuteAsync(
            dataSource, $"ALTER DATABASE {new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database} SET default_transaction_isolation = 'repeatable read'");
        dataSource.Clear();
        Assert.Equal("repeatable read", await Entries.ScalarAsync<string>(dataSource, "SHOW default_transaction_isolation"));
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;

        var results = await Task.WhenAll(Enumerable.Range(1, 10).Select(
            i => Task.Run(() => store.RememberAsync(Entries.Entry(scope, Entries.Graph($"child-{i}"), root.Id), Ct).AsTask(), Ct)));

        Assert.Equal(
            Enumerable.Range(1, 10).Select(minor => $"1.{minor}").Order(StringComparer.Ordinal),
            results.Select(r => r.Value.Version).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EmbeddingShapes))]
    public async Task TheEmbeddingIsStoredWhateverMemoryBacksIt(string shape)
    {
        var store = await MigratedStoreAsync();
        var embedding = Embedding(shape);

        var receipt = (await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope(), embedding: embedding), Ct)).Value;

        Assert.Equal(
            "[1,0.5,-0.25]",
            await Entries.ScalarAsync<string>(fixture.DataSource, "SELECT embedding::text FROM mnemosyne.reasonlets WHERE id = $1", receipt.Id.Value));
    }

    public static TheoryData<string> EmbeddingShapes() => ["array", "offset slice", "prefix slice", "memory manager"];

    [Fact]
    public async Task AnEmbeddingOfTheWrongLengthIsAValidationErrorAndStoresNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var result = await store.RememberAsync(Entries.Entry(scope, embedding: new float[] { 1f, 2f, 3f, 4f }), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Embedding: must have 3 values, the store's dimension, but has 4", result.Error.Message);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1e20f, 0f, 0f)]
    public async Task AnEmbeddingWithoutACosineNormIsAValidationErrorAndStoresNothing(float x, float y, float z)
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var result = await store.RememberAsync(Entries.Entry(scope, embedding: new[] { x, y, z }), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Embedding: must have a nonzero, finite norm, or cosine similarity is undefined", result.Error.Message);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task AnInvalidEntryIsAValidationError()
    {
        var store = await MigratedStoreAsync();
        var entry = Entries.Entry(PgVectorFixture.NewScope()) with { Intent = " " };

        var result = await store.RememberAsync(entry, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Intent: must not be blank", result.Error.Message);
    }

    [Fact]
    public async Task AnEntryWhoseCaptureCarriesAnAnswerIsAValidationErrorAndStoresNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var entry = Entries.Entry(scope);
        entry = entry with { Capture = entry.Capture with { Answer = "the answer" } };

        var result = await store.RememberAsync(entry, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Capture.Answer: must be null; an answer is never stored", result.Error.Message);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task AnInvalidEvaluationIsAValidationErrorAndStoresNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var result = await store.RememberAsync(Entries.Entry(scope) with { Evaluation = new ReasonletEvaluation(true, 1.5, "judge") }, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Evaluation.Score: must be a finite number between 0 and 1", result.Error.Message);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task AnEvaluationIsStoredWithTheFirstRunAndItsVerdictCounts()
    {
        var store = await MigratedStoreAsync();

        // The agent said it worked and the judge said it did not: the judge's verdict counts.
        var result = await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope()) with { Evaluation = new ReasonletEvaluation(false, 0.1, "judge") }, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "t|f|0.1|judge|f",
            await Entries.ScalarAsync<string>(
                fixture.DataSource,
                "SELECT concat_ws('|', agent_success, evaluated_success, evaluation_score, evaluator, success) FROM mnemosyne.reasonlet_executions WHERE id = $1",
                result.Value.Execution.Value));
        Assert.Equal(
            "1|0|-",
            await Entries.ScalarAsync<string>(
                fixture.DataSource,
                "SELECT concat_ws('|', runs, successes, coalesce(last_success_at::text, '-')) FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1",
                result.Value.Id.Value));
    }

    [Fact]
    public async Task ANullEntryThrows()
    {
        var store = await MigratedStoreAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RememberAsync(null!, Ct));
    }

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope()), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var store = await MigratedStoreAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope()), cancelled.Token));
    }

    [Fact]
    public async Task ADeadlockWithAConcurrentTransactionIsAConflict()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;
        var child = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("child"), root.Id), Ct)).Value;

        // The other transaction holds the root. The store locks the child, then waits for the root; the other
        // transaction then asks for the child, closing the cycle. Its long deadlock_timeout makes the store's
        // backend, at the default 1s, the one that detects the deadlock and is aborted.
        await using var other = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await RunAsync(other, "SET LOCAL deadlock_timeout = '30s'");
        await RunAsync(other, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{root.Id.Value}' FOR UPDATE");

        var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("grandchild"), child.Id), Ct).AsTask();
        await WaitForLockWaitAsync(other);
        var closeCycle = RunAsync(other, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{child.Id.Value}' FOR UPDATE");

        var result = await remember;
        await closeCycle;
        await transaction.RollbackAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Contains("40P01", result.Error.Detail, StringComparison.Ordinal);
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    private Task<long> CountAsync(string sql, object parameter) => Entries.ScalarAsync<long>(fixture.DataSource, sql, parameter);

    /// <summary>Whether another transaction holds a lock on the Reasonlet that a FOR UPDATE would wait for.</summary>
    private async Task<bool> IsLockedAsync(ReasonletId id)
    {
        await using var probe = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await probe.BeginTransactionAsync(Ct);
        try
        {
            await RunAsync(probe, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{id.Value}' FOR UPDATE NOWAIT");
            return false;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    /// <summary>Inserts a chain of the given depth in the scope, versions 1.0 down to 1.(depth - 1), and returns its leaf.</summary>
    private async Task<Guid> ChainAsync(ReasonletScope scope, int depth)
    {
        var leaf = Guid.CreateVersion7();
        await Entries.ExecuteAsync(
            fixture.DataSource,
            $$"""
            DO $$
            DECLARE previous uuid; current uuid;
            BEGIN
                FOR i IN 0..{{depth - 1}} LOOP
                    current := CASE WHEN i = {{depth - 1}} THEN '{{leaf}}'::uuid ELSE gen_random_uuid() END;
                    INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding)
                    VALUES (current, '{{scope.Value}}', 'n', 'i', '1.' || i, '{}', repeat('0', 64), previous, 1, 's', now(), '[1,0,0]');
                    previous := current;
                END LOOP;
            END $$
            """);
        return leaf;
    }

    /// <summary>Reads the parent_id of a Reasonlet, or null when it has none.</summary>
    private async Task<Guid?> ParentOfAsync(ReasonletId id)
    {
        var value = await Entries.ScalarAsync<object>(
            fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", id.Value);
        return value as Guid?;
    }

    /// <summary>Reads "version" of every Reasonlet in the subtree under <paramref name="top"/>, top included, sorted.</summary>
    private async Task<List<string>> SubtreeVersionsAsync(ReasonletId top)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "WITH RECURSIVE down AS (SELECT id, version FROM mnemosyne.reasonlets WHERE id = $1 "
            + "UNION ALL SELECT r.id, r.version FROM mnemosyne.reasonlets r JOIN down ON r.parent_id = down.id) "
            + "SELECT version FROM down ORDER BY version COLLATE \"C\"");
        command.Parameters.Add(new NpgsqlParameter { Value = top.Value });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var versions = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }

    private async Task AssertSingleExecutionAsync(ReasonletReceipt receipt, ReasonletCapture capture)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT reasonlet_id, scope, success, duration_ticks, score, source, run_id, subject_id, tags::text, executed_at "
            + "FROM mnemosyne.reasonlet_executions WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = receipt.Execution.Value });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(receipt.Id.Value, reader.GetGuid(0));
        Assert.Equal(capture.Scope.Value, reader.GetString(1));
        Assert.Equal(capture.Outcome.Success, reader.GetBoolean(2));
        Assert.Equal(capture.Outcome.Duration.Ticks, reader.GetInt64(3));
        Assert.Equal(capture.Outcome.Score, reader.IsDBNull(4) ? null : reader.GetDouble(4));
        Assert.Equal(capture.Provenance.Source, reader.GetString(5));
        Assert.Equal(capture.Provenance.RunId, reader.IsDBNull(6) ? null : reader.GetString(6));
        Assert.Equal(capture.Provenance.SubjectId, reader.IsDBNull(7) ? null : reader.GetString(7));
        var tags = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8));
        Assert.Equal(capture.Provenance.Tags?.OrderBy(t => t.Key, StringComparer.Ordinal), tags?.OrderBy(t => t.Key, StringComparer.Ordinal));
        Assert.InRange(reader.GetFieldValue<DateTimeOffset>(9), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.False(await reader.ReadAsync(Ct));
    }

    private static ReadOnlyMemory<float> Embedding(string shape) => shape switch
    {
        "array" => new float[] { 1f, 0.5f, -0.25f },
        "offset slice" => new float[] { 9f, 1f, 0.5f, -0.25f }.AsMemory(1),
        "prefix slice" => new float[] { 1f, 0.5f, -0.25f, 9f }.AsMemory(0, 3),
        _ => new ArrayMemoryManager([1f, 0.5f, -0.25f]).Memory,
    };

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Waits until a backend other than <paramref name="other"/>'s is blocked on a lock.</summary>
    private async Task WaitForLockWaitAsync(NpgsqlConnection other)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await Entries.ScalarAsync<long>(
                fixture.DataSource,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1",
                other.ProcessID);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail("The store never waited for a lock.");
    }

    /// <summary>Memory that is not backed by an array, so MemoryMarshal.TryGetArray fails on it.</summary>
    private sealed class ArrayMemoryManager(float[] values) : MemoryManager<float>
    {
        public override Span<float> GetSpan() => values;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
        }
    }
}
