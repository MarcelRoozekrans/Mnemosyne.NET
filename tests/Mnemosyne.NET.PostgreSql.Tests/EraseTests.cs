using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class EraseTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string SubjectA = "subject-a";
    private const string SubjectB = "subject-b";

    private static readonly ReasonletOutcome Outcome = new(true, TimeSpan.FromMilliseconds(10), 0.5);
    private static readonly ReasonletFeedback Feedback = new(0.5, 0.5, 0.5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ErasingAScopeRemovesEverythingInItCountsExactlyAndLeavesOtherScopesAlone()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = await RememberAsync(store, scope, "root", SubjectA);
        var child = await RememberAsync(store, scope, "child", SubjectB, root.Id);
        var other = await RememberAsync(store, scope, "other", SubjectB);
        var run = await AppendAsync(store, scope, root.Id, SubjectB);
        await AppendAsync(store, scope, other.Id, null);
        Assert.True((await store.AppendFeedbackAsync(scope, run, Feedback, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, child.Execution, Feedback, Ct)).IsSuccess);

        var otherScope = PgVectorFixture.NewScope();
        var kept = await RememberAsync(store, otherScope, "kept", SubjectA);
        await RememberAsync(store, otherScope, "kept-child", SubjectA, kept.Id);
        Assert.True((await store.AppendFeedbackAsync(otherScope, kept.Execution, Feedback, Ct)).IsSuccess);

        var result = await store.EraseAsync(new ReasonletErasure(scope, null), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal((3, 5), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal((0L, 0L, 0L), await CountsAsync(scope));
        Assert.Equal((2L, 2L, 1L), await CountsAsync(otherScope));
    }

    [Fact]
    public async Task ErasingASubjectKeepsOtherSubjectsReasonletsAndRemovesRunsThatFollowTheirProcedure()
    {
        // R is A's root, C is B's child of R, and K is B's own root.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var c = await RememberAsync(store, scope, "c", SubjectB, r.Id);
        var k = await RememberAsync(store, scope, "k", SubjectB);

        // Removed: B's run of A's R goes with R, and A's runs of B's kept C and K go because they are A's.
        var bOnR = await AppendAsync(store, scope, r.Id, SubjectB);
        var aOnC = await AppendAsync(store, scope, c.Id, SubjectA);
        await AppendAsync(store, scope, k.Id, SubjectA);
        Assert.True((await store.AppendFeedbackAsync(scope, bOnR, Feedback, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, aOnC, Feedback, Ct)).IsSuccess);

        // Kept: B's own runs of C and K, an anonymous run of K, and their feedback.
        var bOnK = await AppendAsync(store, scope, k.Id, SubjectB);
        var anonymousOnK = await AppendAsync(store, scope, k.Id, null);
        Assert.True((await store.AppendFeedbackAsync(scope, c.Execution, Feedback, Ct)).IsSuccess);

        // Another scope's data of subject A is untouched.
        var otherScope = PgVectorFixture.NewScope();
        var elsewhere = await RememberAsync(store, otherScope, "elsewhere", SubjectA);
        await AppendAsync(store, otherScope, elsewhere.Id, SubjectA);

        var result = await store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal((1, 4), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal([c.Id.Value, k.Id.Value], await IdsAsync("SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 ORDER BY created_at", scope.Value));
        Assert.Null(await ParentOfAsync(c.Id));
        Assert.Equal(
            new[] { c.Execution.Value, k.Execution.Value, bOnK.Value, anonymousOnK.Value }.Order(),
            (await IdsAsync("SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value)).Order());
        Assert.Equal(
            [c.Execution.Value],
            await IdsAsync("SELECT execution_id FROM mnemosyne.reasonlet_feedback WHERE scope = $1", scope.Value));
        Assert.Equal((1L, 2L, 0L), await CountsAsync(otherScope));
    }

    [Fact]
    public async Task AnOrphanedSubtreeNumbersItsNextVersionWithinItsOwnLineage()
    {
        // R (A) 1.0 <- C (B) 1.1 <- G (B) 1.2, and R <- X (B) 1.3. Erasing A cuts R out, so C and X each top a
        // lineage of their own: C's holds 1.1 and 1.2, so its next child is 1.3; X's holds 1.3, so its next is 1.4.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var c = await RememberAsync(store, scope, "c", SubjectB, r.Id);
        var g = await RememberAsync(store, scope, "g", SubjectB, c.Id);
        var x = await RememberAsync(store, scope, "x", SubjectB, r.Id);
        Assert.Equal(["1.1", "1.2", "1.3"], new[] { c.Version, g.Version, x.Version });

        Assert.True((await store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct)).IsSuccess);
        var underG = await RememberAsync(store, scope, "under-g", SubjectB, g.Id);
        var underX = await RememberAsync(store, scope, "under-x", SubjectB, x.Id);

        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.3"), (underG.Kind, underG.Version));
        Assert.Equal((ReasonletCaptureKind.NewVersion, "1.4"), (underX.Kind, underX.Version));
        Assert.Null(await ParentOfAsync(c.Id));
        Assert.Null(await ParentOfAsync(x.Id));
        Assert.Equal(c.Id.Value, await ParentOfAsync(g.Id));
    }

    [Fact]
    public async Task ASubjectWithNothingToEraseRemovesNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = await RememberAsync(store, scope, "root", SubjectB);
        await RememberAsync(store, scope, "child", SubjectB, root.Id);

        var result = await store.EraseAsync(new ReasonletErasure(scope, "nobody"), Ct);

        Assert.Equal((0, 0), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal((2L, 2L, 0L), await CountsAsync(scope));
    }

    [Fact]
    public async Task AnEmptyScopeErasesNothing()
    {
        var store = await MigratedStoreAsync();

        var result = await store.EraseAsync(new ReasonletErasure(PgVectorFixture.NewScope(), null), Ct);

        Assert.Equal((0, 0), (result.Value.Reasonlets, result.Value.Executions));
    }

    [Theory]
    [MemberData(nameof(InvalidErasures))]
    public async Task AnInvalidErasureIsAValidationError(string? scope, string? subject, string message)
    {
        var store = await MigratedStoreAsync();

        var result = await store.EraseAsync(new ReasonletErasure(scope is null ? default : new ReasonletScope(scope), subject), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(message, result.Error.Message);
    }

    public static TheoryData<string?, string?, string> InvalidErasures() => new()
    {
        { null, null, "Scope.Value: must not be blank" },
        { " ", null, "Scope.Value: must not be blank" },
        { "scope", " ", "SubjectId: must not be blank" },
    };

    [Fact]
    public async Task ANullErasureThrows()
    {
        var store = await MigratedStoreAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EraseAsync(null!, Ct));
    }

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.EraseAsync(new ReasonletErasure(PgVectorFixture.NewScope(), null), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var store = await MigratedStoreAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.EraseAsync(new ReasonletErasure(PgVectorFixture.NewScope(), null), cancelled.Token));
    }

    [Fact]
    public async Task TheEraseLocksDescendantsFirstSoARememberHoldingAChildIsNotDeadlocked()
    {
        // A remember of a grandchild under C holds C and then asks for R, as the path locks go bottom-up. The erase
        // locks C before R, so it waits behind the remember instead of taking R and closing a deadlock.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var c = await RememberAsync(store, scope, "c", SubjectA, r.Id);

        await using var remember = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await remember.BeginTransactionAsync(Ct);
        await RunAsync(remember, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{c.Id.Value}' FOR UPDATE");

        var erase = store.EraseAsync(new ReasonletErasure(scope, null), Ct).AsTask();
        await WaitForLockWaitAsync(remember);
        await RunAsync(remember, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{r.Id.Value}' FOR KEY SHARE");
        await transaction.CommitAsync(Ct);
        var result = await erase;

        Assert.Equal((2, 2), (result.Value.Reasonlets, result.Value.Executions));
    }

    [Fact]
    public async Task AReasonletTheSubjectRemembersWhileTheEraseWaitsSurvivesWithItsExecution()
    {
        // The erase takes its set of A's Reasonlets, then waits for C. A remembers N meanwhile. N is not in the set,
        // so it survives, and so must its first execution: it is A's, but on A's own Reasonlet, which was kept.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var c = await RememberAsync(store, scope, "c", SubjectB, r.Id);

        await using var holder = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await RunAsync(holder, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{c.Id.Value}' FOR UPDATE");

        var erase = store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct).AsTask();
        await WaitForLockWaitAsync(holder);
        var n = await RememberAsync(store, scope, "n", SubjectA);
        await transaction.CommitAsync(Ct);
        var result = await erase;

        Assert.Equal((1, 1), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal([n.Execution.Value], await IdsAsync("SELECT id FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", n.Id.Value));
        Assert.Null(await ParentOfAsync(c.Id));
    }

    [Fact]
    public async Task ADeadlockWithAConcurrentTransactionIsAConflict()
    {
        // The other transaction holds R. The erase locks C, its deepest row, then waits for R; the other transaction
        // then asks for C, closing the cycle. Its long deadlock_timeout makes the erase's backend the one aborted.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var c = await RememberAsync(store, scope, "c", SubjectB, r.Id);

        await using var other = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await RunAsync(other, "SET LOCAL deadlock_timeout = '30s'");
        await RunAsync(other, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{r.Id.Value}' FOR UPDATE");

        var erase = store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct).AsTask();
        await WaitForLockWaitAsync(other);
        var closeCycle = RunAsync(other, $"SELECT id FROM mnemosyne.reasonlets WHERE id = '{c.Id.Value}' FOR UPDATE");

        var result = await erase;
        await closeCycle;
        await transaction.RollbackAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Contains("40P01", result.Error.Detail, StringComparison.Ordinal);
        Assert.Equal((2L, 2L, 0L), await CountsAsync(scope));
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    private static async Task<ReasonletReceipt> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, string subject, ReasonletId? parent = null)
    {
        var provenance = new ReasonletProvenance { Source = "test", SubjectId = subject };
        var result = await store.RememberAsync(Entries.Entry(scope, Entries.Graph(variant), parent, provenance: provenance), Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<ExecutionId> AppendAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, ReasonletId id, string? subject)
    {
        var result = await store.AppendExecutionAsync(scope, id, Outcome, new ReasonletProvenance { Source = "test", SubjectId = subject }, Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>Counts a scope's Reasonlets, executions and feedback.</summary>
    private async Task<(long Reasonlets, long Executions, long Feedback)> CountsAsync(ReasonletScope scope) => (
        await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value),
        await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value),
        await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE scope = $1", scope.Value));

    private async Task<List<Guid>> IdsAsync(string sql, object parameter)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<Guid?> ParentOfAsync(ReasonletId id) =>
        await Entries.ScalarAsync<object>(fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", id.Value) as Guid?;

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

        Assert.Fail("The erase never waited for a lock.");
    }
}
