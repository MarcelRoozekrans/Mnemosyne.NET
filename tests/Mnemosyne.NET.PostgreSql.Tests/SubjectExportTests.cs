using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>Reading everything a subject left in a scope, against a real database.</summary>
public sealed class SubjectExportTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string Subject = "subject-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllOfASubjectsReasonletsComeBackInEveryStatus()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var active = await RememberAsync(store, scope, "a");
        var pending = await RememberAsync(store, scope, "b", review: true);
        var version = await RememberAsync(store, scope, "c", parent: active.Id);

        var listed = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal(new[] { active.Id, pending.Id, version.Id }.OrderBy(i => i.Value), listed.Select(r => r.Definition.Id));
        foreach (var record in listed)
        {
            var expected = (await store.GetAsync(scope, record.Definition.Id, Ct)).Value;
            var actual = record.Definition;
            Assert.Equal(
                (expected.Name, expected.Intent, expected.Version, expected.Status, expected.ParentId, expected.CreatedAt),
                (actual.Name, actual.Intent, actual.Version, actual.Status, actual.ParentId, actual.CreatedAt));
            Assert.Equal(expected.Provenance.SubjectId, actual.Provenance.SubjectId);
            Assert.Equal(expected.Provenance.Tags, actual.Provenance.Tags);
            Assert.Equal(expected.Graph.Nodes.Count, actual.Graph.Nodes.Count);
        }

        Assert.Equal(ReasonletStatus.PendingReview, listed.Single(r => r.Definition.Id == pending.Id).Definition.Status);
    }

    [Fact]
    public async Task OnlyTheAskedScopeAndSubjectAreRead()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var other = PgVectorFixture.NewScope();
        var mine = await RememberAsync(store, scope, "a");
        await RememberAsync(store, other, "a");
        await RememberAsync(store, scope, "b", subject: "subject-2");

        var listed = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal([mine.Id], listed.Select(r => r.Definition.Id));
    }

    [Fact]
    public async Task ASubjectIdThatDiffersInCaseMatchesNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await RememberAsync(store, scope, "a");

        var listed = await store.ListSubjectReasonletsAsync(scope, "SUBJECT-1", 10, null, Ct);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Theory]
    [InlineData(5, 2, new[] { 2, 2, 1, 0 })]
    [InlineData(4, 2, new[] { 2, 2, 0 })]
    public async Task PagesCoverTheSetWithoutRepeatOrGap(int count, int limit, int[] sizes)
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var all = new List<ReasonletId>();
        for (var i = 0; i < count; i++)
        {
            all.Add((await RememberAsync(store, scope, $"v{i}")).Id);
        }

        var seen = new List<ReasonletId>();
        var pageSizes = new List<int>();
        ReasonletId? after = null;
        while (true)
        {
            var page = (await store.ListSubjectReasonletsAsync(scope, Subject, limit, after, Ct)).Value;
            pageSizes.Add(page.Count);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(r => r.Definition.Id));
            after = page[^1].Definition.Id;
        }

        Assert.Equal(sizes, pageSizes);
        Assert.Equal(all.OrderBy(i => i.Value), seen);
    }

    [Fact]
    public async Task AReasonletWithoutAMetricsRowReadsAsZeroRuns()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var bare = await RememberAsync(store, scope, "bare");
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", bare.Id.Value);

        var record = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value.Single();

        var metrics = record.Metrics;
        Assert.Equal((0, 0, 0, 0), (metrics.Runs, metrics.Successes, metrics.JudgedRuns, metrics.JudgedSuccesses));
        Assert.Null(metrics.LastSuccessAt);
        Assert.Equal(record.Definition.CreatedAt, metrics.LastExecutedAt);
    }

    [Fact]
    public async Task ASubjectWithNothingReturnsAnEmptySuccess()
    {
        var store = await MigratedStoreAsync();

        var listed = await store.ListSubjectReasonletsAsync(PgVectorFixture.NewScope(), Subject, 10, null, Ct);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task ValidationAndCancellation()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var results = new[]
        {
            await store.ListSubjectReasonletsAsync(default, Subject, 10, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, " ", 10, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 0, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 101, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 10, default(ReasonletId), Ct),
        };

        Assert.All(results, r => Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code));
        Assert.True((await store.ListSubjectReasonletsAsync(scope, Subject, 100, null, Ct)).IsSuccess);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, cancelled.Token));
    }

    [Fact]
    public async Task AnUnmigratedStoreAnswersUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.ListSubjectReasonletsAsync(PgVectorFixture.NewScope(), Subject, 10, null, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task AFailedDataSourceAnswersUnavailable()
    {
        var reasonlets = await AfterTheSessionsEndAsync((store, scope) => store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct).AsTask());
        var runs = await AfterTheSessionsEndAsync((store, scope) => store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct).AsTask());

        Assert.Equal(MnemosyneErrorCode.Unavailable, reasonlets.Error.Code);
        Assert.Equal(MnemosyneErrorCode.Unavailable, runs.Error.Code);
    }

    // The pool keeps the migrated connections open. Ending their sessions makes the next use of one fail, and the pool
    // then drops it, so each call gets a database of its own.
    private async Task<Result<T, MnemosyneError>> AfterTheSessionsEndAsync<T>(
        Func<ReasonletPostgreSqlStore, ReasonletScope, Task<Result<T, MnemosyneError>>> call)
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        await RememberAsync(store, scope, "a");

        var database = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database!;
        const string Sessions = "FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()";
        await Entries.ExecuteAsync(fixture.DataSource, $"SELECT pg_terminate_backend(pid) {Sessions}", database);
        for (var i = 0; await Entries.ScalarAsync<long>(fixture.DataSource, $"SELECT count(*) {Sessions}", database) > 0; i++)
        {
            Assert.True(i < 500, "sessions did not end");
            await Task.Delay(10, Ct);
        }

        return await call(store, scope);
    }

    [Fact]
    public async Task ARunByTheSubjectOnAnotherOwnersReasonletIsExportedWithoutThatReasonlet()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var theirs = await RememberAsync(store, scope, "a", subject: "owner");
        var run = await AppendAsync(store, scope, theirs.Id, Subject);

        var runs = (await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct)).Value;
        var reasonlets = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value;

        var exported = Assert.Single(runs);
        Assert.Equal((run, theirs.Id, scope), (exported.Id, exported.ReasonletId, exported.Scope));
        Assert.Equal(Subject, exported.Provenance.SubjectId);
        Assert.Empty(reasonlets);
    }

    [Fact]
    public async Task ARunByAnotherSubjectOnTheSubjectsReasonletIsExported()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var mine = await RememberAsync(store, scope, "a");
        var visitor = await AppendAsync(store, scope, mine.Id, "visitor");

        var runs = (await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal(new[] { mine.Execution, visitor }.OrderBy(i => i.Value), runs.Select(r => r.Id));
        Assert.Equal("visitor", runs.Single(r => r.Id == visitor).Provenance.SubjectId);
    }

    [Fact]
    public async Task EveryVerdictInputComesBack()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var owner = await RememberAsync(store, scope, "a", subject: "owner");
        var tags = new Dictionary<string, string> { ["k"] = "v" };

        var agentOutcome = new ReasonletOutcome(false, TimeSpan.FromTicks(12345), 0.4);
        var agentOnly = await AppendAsync(store, scope, owner.Id, Subject, agentOutcome, tags: tags);
        var judge = new ReasonletEvaluation(true, 0.9, "judge");
        var judged = await AppendAsync(store, scope, owner.Id, Subject, new ReasonletOutcome(false, TimeSpan.Zero, null), judge);
        var worked = new ReasonletFeedback(0.1, 0.2, 0.3, false);
        var human = await AppendAsync(store, scope, owner.Id, Subject, new ReasonletOutcome(true, TimeSpan.FromTicks(7), 1), feedback: worked);
        var silent = new ReasonletFeedback(0.5, 0.6, 0.7);
        var scored = await AppendAsync(store, scope, owner.Id, Subject, new ReasonletOutcome(false, TimeSpan.Zero, null), judge, silent);

        var runs = (await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct)).Value.ToDictionary(r => r.Id);

        Assert.Equal(4, runs.Count);
        var a = runs[agentOnly];
        Assert.Equal((false, ReasonletVerdictSource.Agent, agentOutcome), (a.Success, a.VerdictSource, a.Outcome));
        Assert.Null(a.Evaluation);
        Assert.Null(a.Feedback);
        Assert.Null(a.FeedbackRecordedAt);
        Assert.Equal(tags, a.Provenance.Tags);
        Assert.Equal("test", a.Provenance.Source);
        var b = runs[judged];
        Assert.Equal((true, ReasonletVerdictSource.Evaluator, judge), (b.Success, b.VerdictSource, b.Evaluation));
        Assert.Null(b.Feedback);
        Assert.Null(b.Provenance.Tags);
        var c = runs[human];
        Assert.Equal((false, ReasonletVerdictSource.Feedback, worked), (c.Success, c.VerdictSource, c.Feedback));
        Assert.Null(c.Evaluation);
        Assert.NotNull(c.FeedbackRecordedAt);
        Assert.Equal(new ReasonletOutcome(true, TimeSpan.FromTicks(7), 1), c.Outcome);
        var d = runs[scored];
        Assert.Equal((true, ReasonletVerdictSource.Evaluator, judge, silent), (d.Success, d.VerdictSource, d.Evaluation, d.Feedback));
        Assert.Null(d.Feedback!.Value.Worked);
        Assert.NotNull(d.FeedbackRecordedAt);
    }

    [Fact]
    public async Task ALaterFeedbackWithoutAVerdictKeepsTheEarlierOne()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var owner = await RememberAsync(store, scope, "a", subject: "owner");
        var run = await AppendAsync(store, scope, owner.Id, Subject, feedback: new ReasonletFeedback(0.1, 0.2, 0.3, true));
        Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.4, 0.5, 0.6), Ct)).IsSuccess);

        var exported = (await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct)).Value.Single();

        Assert.Equal(ReasonletVerdictSource.Feedback, exported.VerdictSource);
        var feedback = exported.Feedback!.Value;
        Assert.True(feedback.Worked);
        Assert.Equal(0.4, feedback.Utility);
    }

    [Fact]
    public async Task OnlyTheAskedScopeAndSubjectAreReadForRuns()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var other = PgVectorFixture.NewScope();
        var mine = await RememberAsync(store, scope, "a");
        await RememberAsync(store, other, "a");
        await RememberAsync(store, scope, "b", subject: "subject-2");

        var runs = (await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal([mine.Execution], runs.Select(r => r.Id));
    }

    [Fact]
    public async Task ASubjectIdThatDiffersInCaseMatchesNoRuns()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await RememberAsync(store, scope, "a");

        var runs = await store.ListSubjectExecutionsAsync(scope, "SUBJECT-1", 10, null, Ct);

        Assert.True(runs.IsSuccess);
        Assert.Empty(runs.Value);
    }

    [Theory]
    [InlineData(5, 2, new[] { 2, 2, 1, 0 })]
    [InlineData(4, 2, new[] { 2, 2, 0 })]
    public async Task RunPagesCoverTheSetWithoutRepeatOrGap(int count, int limit, int[] sizes)
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var all = new List<ExecutionId>();
        for (var i = 0; i < count; i++)
        {
            all.Add((await RememberAsync(store, scope, $"v{i}")).Execution);
        }

        var seen = new List<ExecutionId>();
        var pageSizes = new List<int>();
        ExecutionId? after = null;
        while (true)
        {
            var page = (await store.ListSubjectExecutionsAsync(scope, Subject, limit, after, Ct)).Value;
            pageSizes.Add(page.Count);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(r => r.Id));
            after = page[^1].Id;
        }

        Assert.Equal(sizes, pageSizes);
        Assert.Equal(all.OrderBy(i => i.Value), seen);
    }

    [Fact]
    public async Task RunValidationAndCancellation()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var results = new[]
        {
            await store.ListSubjectExecutionsAsync(default, Subject, 10, null, Ct),
            await store.ListSubjectExecutionsAsync(scope, " ", 10, null, Ct),
            await store.ListSubjectExecutionsAsync(scope, Subject, 0, null, Ct),
            await store.ListSubjectExecutionsAsync(scope, Subject, 101, null, Ct),
            await store.ListSubjectExecutionsAsync(scope, Subject, 10, default(ExecutionId), Ct),
        };

        Assert.All(results, r => Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code));
        Assert.True((await store.ListSubjectExecutionsAsync(scope, Subject, 100, null, Ct)).IsSuccess);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.ListSubjectExecutionsAsync(scope, Subject, 10, null, cancelled.Token));
    }

    [Fact]
    public async Task AnUnmigratedStoreAnswersUnavailableForRuns()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.ListSubjectExecutionsAsync(PgVectorFixture.NewScope(), Subject, 10, null, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task TheExportEqualsWhatAnEraseRemoves()
    {
        const string A = "subject-a";
        const string B = "subject-b";
        const string C = "subject-c";
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var a1 = await RememberAsync(store, scope, "a1", subject: A);
        var a2 = await RememberAsync(store, scope, "a2", subject: A);
        await RememberAsync(store, scope, "a1v", parent: a1.Id, subject: A);
        var b1 = await RememberAsync(store, scope, "b1", subject: B);
        var b2 = await RememberAsync(store, scope, "b2", subject: B);
        var c1 = await RememberAsync(store, scope, "c1", subject: C);
        await AppendAsync(store, scope, a1.Id, B);
        await AppendAsync(store, scope, a2.Id, C);
        await AppendAsync(store, scope, a2.Id, A);
        await AppendAsync(store, scope, b1.Id, A);
        await AppendAsync(store, scope, b1.Id, A);
        await AppendAsync(store, scope, b2.Id, C);
        await AppendAsync(store, scope, c1.Id, A);
        await AppendAsync(store, scope, c1.Id, B);
        var bOnA = await RememberAsync(store, scope, "bOnA", parent: a1.Id, subject: B);
        await AppendAsync(store, scope, bOnA.Id, A);
        var aOnB = await RememberAsync(store, scope, "aOnB", parent: b1.Id, subject: A);
        await AppendAsync(store, scope, aOnB.Id, B);

        var exportedRecords = new List<ReasonletRecord>();
        var exportedReasonlets = new List<Guid>();
        ReasonletId? afterReasonlet = null;
        while ((await store.ListSubjectReasonletsAsync(scope, A, 2, afterReasonlet, Ct)).Value is { Count: > 0 } page)
        {
            exportedRecords.AddRange(page);
            exportedReasonlets.AddRange(page.Select(r => r.Definition.Id.Value));
            afterReasonlet = page[^1].Definition.Id;
        }

        var exportedRuns = new List<Guid>();
        ExecutionId? afterRun = null;
        while ((await store.ListSubjectExecutionsAsync(scope, A, 3, afterRun, Ct)).Value is { Count: > 0 } page)
        {
            exportedRuns.AddRange(page.Select(r => r.Id.Value));
            afterRun = page[^1].Id;
        }

        var allReasonlets = await IdsAsync("SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value);
        var allRuns = await IdsAsync("SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value);

        var erased = (await store.EraseAsync(new ReasonletErasure(scope, A), Ct)).Value;

        Assert.Equal((exportedReasonlets.Count, exportedRuns.Count), (erased.Reasonlets, erased.Executions));
        Assert.NotEmpty(exportedReasonlets);
        Assert.NotEmpty(exportedRuns);
        var reasonletsAfter = await IdsAsync("SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value);
        var runsAfter = await IdsAsync("SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value);
        Assert.Equal(allReasonlets.Except(exportedReasonlets).Order(), reasonletsAfter.Order());
        Assert.Equal(allRuns.Except(exportedRuns).Order(), runsAfter.Order());
        Assert.Empty(exportedReasonlets.Intersect(reasonletsAfter));
        Assert.Empty(exportedRuns.Intersect(runsAfter));

        // Lineage across owners: a version B captured on A's Reasonlet is B's, so it stays and loses its parent.
        Assert.DoesNotContain(bOnA.Id.Value, exportedReasonlets);
        Assert.Contains(bOnA.Id.Value, reasonletsAfter);
        Assert.Null((await store.GetAsync(scope, bOnA.Id, Ct)).Value.ParentId);
        // A version A captured on B's Reasonlet is A's: exported with its parent named, erased, and B's parent stays.
        Assert.Equal(b1.Id, exportedRecords.Single(r => r.Definition.Id == aOnB.Id).Definition.ParentId);
        Assert.DoesNotContain(aOnB.Id.Value, reasonletsAfter);
        Assert.Contains(b1.Id.Value, reasonletsAfter);
    }

    private async Task<List<Guid>> IdsAsync(string sql, params object[] parameters)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<ExecutionId> AppendAsync(
        ReasonletPostgreSqlStore store,
        ReasonletScope scope,
        ReasonletId id,
        string subject,
        ReasonletOutcome? outcome = null,
        ReasonletEvaluation? evaluation = null,
        ReasonletFeedback? feedback = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        var provenance = new ReasonletProvenance { Source = "test", SubjectId = subject, Tags = tags };
        var appended = await store.AppendExecutionAsync(scope, id, outcome ?? new ReasonletOutcome(true, TimeSpan.Zero, null), provenance, evaluation, Ct);
        Assert.True(appended.IsSuccess);
        if (feedback is { } recorded)
        {
            Assert.True((await store.AppendFeedbackAsync(scope, appended.Value.Execution, recorded, Ct)).IsSuccess);
        }

        return appended.Value.Execution;
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    private static async Task<ReasonletReceipt> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, ReasonletId? parent = null, bool review = false, string subject = Subject)
    {
        var provenance = Entries.Provenance() with { SubjectId = subject };
        var entry = Entries.Entry(scope, Entries.Graph(variant), parent, provenance: provenance) with { ReviewRequired = review };
        var result = await store.RememberAsync(entry, Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }
}
