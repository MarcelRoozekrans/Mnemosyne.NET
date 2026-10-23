using System.Data;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Repositories;
using Mnemosyne.NET.PostgreSql.Rows;
using Npgsql;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql;

/// <summary>An <see cref="IReasonletStore"/> on PostgreSQL with pgvector.</summary>
/// <remarks>
/// The host calls <see cref="MigrateAsync"/> once at startup, before anything else. Every other member answers
/// <see cref="MnemosyneErrorCode.Unavailable"/> until the schema is migrated. The store holds at most one
/// connection at a time from the data source it is given; a prune opens several in sequence. Every public member is one operation of
/// <see cref="MnemosyneTelemetry.ActivitySourceName"/> and <see cref="MnemosyneTelemetry.MeterName"/>, a failed or
/// invalid call included, and its signals carry no user data.
/// </remarks>
public sealed class ReasonletPostgreSqlStore : IReasonletStore
{
    private const int MaxSearchLimit = 200;
    private const int DefaultEfSearch = 40;
    private const string MigrationResourcePrefix = "Mnemosyne.NET.PostgreSql.Migrations.";

    /// <summary>The schema version this store needs, which the newest migration records in <c>mnemosyne.settings</c>.</summary>
    private const int SchemaVersion = 2;

    /// <summary>The most rows one prune transaction deletes, so no batch holds its locks for long.</summary>
    internal const int PruneBatchSize = 1000;

    /// <summary>The number of times a prune batch that deadlocked or failed to serialize is tried again.</summary>
    private const int PruneRetries = 3;

    private readonly NpgsqlDataSource _dataSource;
    private readonly MnemosynePostgreSqlOptions _options;
    private volatile bool _migrated;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">
    /// The data source to open connections from. Hosts that publish with native AOT should build it with
    /// <c>NpgsqlSlimDataSourceBuilder</c> and call <c>EnableArrays()</c>, because embeddings bind as arrays.
    /// </param>
    /// <param name="options">The store's settings. They are validated here.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public ReasonletPostgreSqlStore(NpgsqlDataSource dataSource, MnemosynePostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _dataSource = dataSource;
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>It is <see cref="MnemosynePostgreSqlOptions.Dimensions"/>, which the first <see cref="MigrateAsync"/> records.</remarks>
    public int Dimensions => _options.Dimensions;

    /// <summary>Creates or updates the <c>mnemosyne</c> schema and applies the embedding dimension and HNSW options.</summary>
    /// <remarks>
    /// Calling it again is harmless. The server must run PostgreSQL 15 or later and pgvector 0.8.0 or later, or the
    /// call fails with <see cref="MnemosyneErrorCode.Validation"/>. The first call fixes the dimension at
    /// <see cref="MnemosynePostgreSqlOptions.Dimensions"/> and builds the HNSW index with
    /// <see cref="MnemosynePostgreSqlOptions.HnswM"/> and <see cref="MnemosynePostgreSqlOptions.HnswEfConstruction"/>.
    /// A later call with a different dimension fails with <see cref="MnemosyneErrorCode.Validation"/> and changes
    /// nothing, because mixing embedding models would corrupt similarity; so does a later call with different HNSW
    /// options, because they apply only when the index is built. A database failure answers
    /// <see cref="MnemosyneErrorCode.Unavailable"/>.
    /// </remarks>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or the error.</returns>
    public ValueTask<UnitResult<MnemosyneError>> MigrateAsync(CancellationToken ct) =>
        StoreTelemetry.RunAsync("migrate", _ => MigrateOperationAsync(ct));

    private async ValueTask<UnitResult<MnemosyneError>> MigrateOperationAsync(CancellationToken ct)
    {
        // Assigned rather than returned inside the try, so every line of the method can execute.
        UnitResult<MnemosyneError> result;
        try
        {
            result = await MigrateCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    private async Task<UnitResult<MnemosyneError>> MigrateCoreAsync(CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();

        // Before the migrations, which an older server cannot even parse.
        var server = await new SettingsRepository(connection).GetServerVersionAsync(ct).ConfigureAwait(false);
        if (!PostgresVersion.IsSupported(server!.Number))
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                $"PostgreSQL {server.Text} is running, but the store needs PostgreSQL {PostgresVersion.MinimumMajor} or later for its scoped foreign keys.");
        }

        var source = new EmbeddedResourceMigrationSource(typeof(ReasonletPostgreSqlStore).Assembly, MigrationResourcePrefix);
        await new MigrationRunner(connection, source, new PostgresMigrationDialect()).RunAsync(ct).ConfigureAwait(false);

        var vectorVersion = await new SettingsRepository(connection).GetVectorVersionAsync(ct).ConfigureAwait(false);
        if (!PgVectorVersion.IsSupported(vectorVersion))
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                $"pgvector {vectorVersion ?? "(not installed)"} is installed, but the store needs pgvector {PgVectorVersion.Minimum} or later for iterative index scans.");
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var settings = new SettingsRepository(connection);
        await settings.LockAsync(transaction, ct).ConfigureAwait(false);

        // One row, whatever is recorded, so the null-forgiving operator only skips a check the query cannot fail.
        var recorded = (await settings.GetAsync(transaction, ct).ConfigureAwait(false))!;
        if (recorded.Dimensions is { } dimensions)
        {
            if (dimensions != _options.Dimensions)
            {
                return DimensionMismatch(dimensions);
            }

            if (recorded.HnswM != _options.HnswM || recorded.HnswEfConstruction != _options.HnswEfConstruction)
            {
                return HnswMismatch(recorded);
            }
        }
        else
        {
            await ApplyDimensionsAsync(connection, transaction, ct).ConfigureAwait(false);
            await settings.RecordAsync(_options.Dimensions, _options.HnswM, _options.HnswEfConstruction, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        _migrated = true;
        return UnitResult<MnemosyneError>.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One transaction at READ COMMITTED decides between a new Reasonlet, an execution only and a new version, and
    /// counts the execution in the Reasonlet's metrics. A new version takes row locks going up its lineage, in this
    /// order: the parent <c>FOR NO KEY UPDATE</c>, then every ancestor between the parent and the top of the lineage
    /// <c>FOR KEY SHARE</c>, bottom-up, then the top <c>FOR NO KEY UPDATE</c>. The top's lock serializes concurrent
    /// children of the lineage, so their versions never collide, and the ancestor locks keep an erase from cutting
    /// the path to the top until commit. An execution only locks the parent and then the parent's metrics row, which it
    /// holds until commit. A new version that loses a race
    /// with a concurrent erase of its lineage answers <see cref="MnemosyneErrorCode.Conflict"/>.
    /// </para>
    /// <para>
    /// The embedding must have <see cref="MnemosynePostgreSqlOptions.Dimensions"/> values and a nonzero, finite norm,
    /// or the entry is a <see cref="MnemosyneErrorCode.Validation"/> failure: cosine similarity is undefined otherwise.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The stored lineage is corrupt: the <c>parent_id</c> links above the parent form a cycle, which only
    /// hand-written SQL can create.
    /// </exception>
    public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct) =>
        StoreTelemetry.RunAsync("remember", activity => RememberOperationAsync(entry, activity, ct));

    private async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberOperationAsync(
        ReasonletStoreEntry entry, Activity? activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var valid = entry.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        if (entry.Embedding.Length != _options.Dimensions)
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{nameof(entry.Embedding)}: must have {_options.Dimensions} values, the store's dimension, but has {entry.Embedding.Length}"));
        }

        if (!HasCosineNorm(entry.Embedding.Span))
        {
            return NormError(nameof(entry.Embedding));
        }

        var result = await GuardAsync(RememberCoreAsync(entry, ct)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            StoreTelemetry.Remember(activity, result.Value);
        }

        return result;
    }

    private async Task<Result<ReasonletReceipt, MnemosyneError>> RememberCoreAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        var capture = entry.Capture;

        // The hash is of the capture's graph and is never recomputed from stored text, so a graph read back unchanged matches.
        var hash = OrThrow(capture.Graph.ComputeContentHash(), "Hashing a validated graph");

        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();

        // Named rather than left to a default. The row locks serialize the writes, and each statement must then see
        // what the lock holder committed: at REPEATABLE READ a waiting child would miss its sibling's version.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var reasonlets = new ReasonletRepository(connection);
        var executions = new ExecutionRepository(connection);

        ReasonletReceipt receipt;
        if (capture.ParentId is not { } parentId)
        {
            receipt = await InsertReasonletAsync(
                reasonlets, executions, entry, hash, LineageVersions.Initial, null, ReasonletCaptureKind.NewReasonlet, transaction, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var parent = await reasonlets.LockParentAsync(capture.Scope.Value, parentId.Value, transaction, ct).ConfigureAwait(false);
            if (parent is null)
            {
                return new MnemosyneError(MnemosyneErrorCode.NotFound, "The parent Reasonlet does not exist in the scope.");
            }

            if (string.Equals(parent.ContentHash, hash, StringComparison.Ordinal))
            {
                var execution = await InsertExecutionAsync(executions, parent.Id, capture, entry.Evaluation, transaction, ct).ConfigureAwait(false);
                receipt = new ReasonletReceipt(
                    new ReasonletId(parent.Id), parent.Version, execution, ReasonletCaptureKind.ExecutionOnly, (ReasonletStatus)parent.Status);
            }
            else
            {
                // The lineage is the top above the parent and the top's whole subtree, derived from parent_id within the
                // scope. Every lock is taken going up the tree: the parent FOR NO KEY UPDATE, then each ancestor between
                // the parent and the top FOR KEY SHARE, bottom-up, then the top FOR NO KEY UPDATE. When the parent is the
                // top, the top lock is a re-lock. A transaction only ever waits for a row above every row it holds, so
                // remembers cannot deadlock one another.
                var scope = capture.Scope.Value;
                var path = FollowToTop(parent.Id, await reasonlets.WalkUpAsync(scope, parent.Id, transaction, ct).ConfigureAwait(false));

                // The path locks hold every link from the parent to the top until commit, because an erase must delete
                // an ancestor to cut a link. A row that vanished between the walk and its lock means a concurrent erase
                // already cut the path, so the walk's top and subtree are stale: a lost race with an erase.
                // Between the parent and the top; none when the parent is the top or its child.
                var ancestors = path[1..Math.Max(1, path.Length - 1)];
                if (ancestors.Length > 0
                    && await reasonlets.LockAncestorsAsync(scope, ancestors, transaction, ct).ConfigureAwait(false) != ancestors.Length)
                {
                    return LineageConflict();
                }

                var top = path[^1];
                if (await reasonlets.LockLineageTopAsync(scope, top, transaction, ct).ConfigureAwait(false) is null)
                {
                    return LineageConflict();
                }

                var lineage = (await reasonlets.GetLineageMinorAsync(scope, top, transaction, ct).ConfigureAwait(false))!;
                var version = LineageVersions.Next(lineage.MaxMinor, lineage.Malformed);
                receipt = await InsertReasonletAsync(
                    reasonlets, executions, entry, hash, version, parent.Id, ReasonletCaptureKind.NewVersion, transaction, ct)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// Follows the links of a walk up from <paramref name="start"/> to the top of its lineage, and returns the path:
    /// <paramref name="start"/> first and the top last.
    /// </summary>
    /// <exception cref="InvalidOperationException">The links lead back to a Reasonlet already on the path.</exception>
    private static Guid[] FollowToTop(Guid start, List<ParentLinkRow> links)
    {
        var parents = new Dictionary<Guid, Guid?>(links.Count);
        foreach (var link in links)
        {
            parents[link.Id] = link.ParentId;
        }

        var path = new List<Guid>(links.Count) { start };
        var visited = new HashSet<Guid> { start };
        while (parents[path[^1]] is { } next)
        {
            if (!visited.Add(next))
            {
                // The store never links a Reasonlet to a descendant, so a cycle is corrupt data: a bug, not a result.
                throw new InvalidOperationException(
                    $"The parent_id links above Reasonlet {start} form a cycle through {next}; the stored lineage is corrupt.");
            }

            path.Add(next);
        }

        return [.. path];
    }

    private static async Task<ReasonletReceipt> InsertReasonletAsync(
        ReasonletRepository reasonlets,
        ExecutionRepository executions,
        ReasonletStoreEntry entry,
        string hash,
        string version,
        Guid? parentId,
        ReasonletCaptureKind kind,
        IAsyncDbTransaction transaction,
        CancellationToken ct)
    {
        var capture = entry.Capture;
        var provenance = capture.Provenance;
        var id = ReasonletId.New();
        await reasonlets.InsertAsync(
            id.Value,
            capture.Scope.Value,
            entry.Name,
            entry.Intent,
            version,
            ReasonletGraphJson.Serialize(capture.Graph),
            hash,
            parentId,
            (short)ReasonletStatus.Active,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            ToArray(entry.Embedding),
            transaction,
            ct).ConfigureAwait(false);
        var execution = await InsertExecutionAsync(executions, id.Value, capture, entry.Evaluation, transaction, ct).ConfigureAwait(false);
        return new ReasonletReceipt(id, version, execution, kind, ReasonletStatus.Active);
    }

    /// <summary>
    /// Inserts the capture's execution of a Reasonlet this transaction holds or has just inserted, with the evaluator's
    /// verdict on it when there is one, and counts its effective success in the Reasonlet's metrics.
    /// </summary>
    private static async Task<ExecutionId> InsertExecutionAsync(
        ExecutionRepository executions,
        Guid reasonletId,
        ReasonletCapture capture,
        ReasonletEvaluation? evaluation,
        IAsyncDbTransaction transaction,
        CancellationToken ct)
    {
        var id = ExecutionId.New();
        var outcome = capture.Outcome;
        var provenance = capture.Provenance;
        await executions.InsertAsync(
            id.Value,
            reasonletId,
            capture.Scope.Value,
            outcome.Success,
            evaluation?.Success,
            evaluation?.Score,
            evaluation?.Evaluator,
            outcome.Duration.Ticks,
            outcome.Score,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            transaction,
            ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) =>
        StoreTelemetry.RunAsync("get", _ => GetOperationAsync(scope, id, ct));

    private async ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetOperationAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var validScope = scope.Validate();
        if (validScope.IsFailure)
        {
            return validScope.Error with { Message = $"{nameof(scope)}.{validScope.Error.Message}" };
        }

        if (id.IsDefault)
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(id)}: must not be the default id");
        }

        return await GuardAsync(GetCoreAsync(scope, id, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ReasonletDefinition, MnemosyneError>> GetCoreAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var row = await new ReasonletRepository(connection).GetAsync(scope.Value, id.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The Reasonlet does not exist in the scope.");
        }

        return ToDefinition(row);
    }

    private static ReasonletDefinition ToDefinition(ReasonletRow row)
    {
        return new ReasonletDefinition
        {
            Id = new ReasonletId(row.Id),
            Scope = new ReasonletScope(row.Scope),
            Name = row.Name,
            Intent = row.Intent,
            Version = row.Version,

            // The store wrote this text from a validated graph, so text that no longer reads is a bug, not a result.
            Graph = OrThrow(ReasonletGraphJson.Deserialize(row.Graph), $"Reading the stored graph of Reasonlet {row.Id}"),
            Provenance = new ReasonletProvenance
            {
                Source = row.Source,
                RunId = row.RunId,
                SubjectId = row.SubjectId,
                Tags = row.Tags is null ? null : JsonSerializer.Deserialize(row.Tags, TagsJson.Default.DictionaryStringString),
            },
            Status = (ReasonletStatus)row.Status,
            CreatedAt = row.CreatedAt,
            ParentId = row.ParentId is { } parent ? new ReasonletId(parent) : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// One scoped statement inserts the execution from the Reasonlet's row and counts it in the Reasonlet's metrics,
    /// so a Reasonlet that is missing or in another scope answers <see cref="MnemosyneErrorCode.NotFound"/> without a
    /// prior read. The statement takes the Reasonlet <c>FOR KEY SHARE</c>, which a remember's Reasonlet locks let
    /// through, so an append does not wait for a new version anywhere in the lineage. It then updates the
    /// Reasonlet's metrics row, so it waits for any transaction that holds that row until commit: an execution-only
    /// remember of the same Reasonlet, another append to it, or an erase that is recounting it as a kept Reasonlet.
    /// It also waits for an erase that holds the Reasonlet itself, and a Reasonlet that erase deletes answers
    /// <see cref="MnemosyneErrorCode.NotFound"/>. The run stores the agent's flag, and the evaluator's success, score
    /// and name when <paramref name="evaluation"/> is given; its effective success is the evaluator's verdict when
    /// there is one and the agent's otherwise, and the metrics count that. The <c>mnemosyne.execution.append</c> activity
    /// carries <c>mnemosyne.verdict_source</c>: <c>evaluator</c> when <paramref name="evaluation"/> is given, else <c>agent</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is null.</exception>
    public ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct) =>
        StoreTelemetry.RunAsync("execution.append", activity =>
        {
            StoreTelemetry.ExecutionVerdictSource(activity, evaluation is not null);
            return AppendExecutionOperationAsync(scope, id, outcome, provenance, evaluation, ct);
        });

    private async ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionOperationAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope)
            ?? ValidateId(id)
            ?? ValidateNested(outcome.Validate(), nameof(outcome))
            ?? ValidateNested(provenance.Validate(), nameof(provenance))
            ?? (evaluation is { } verdict ? ValidateNested(verdict.Validate(), nameof(evaluation)) : null);
        if (invalid is { } error)
        {
            return error;
        }

        return await GuardAsync(AppendExecutionCoreAsync(scope, id, outcome, provenance, evaluation, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ExecutionId, MnemosyneError>> AppendExecutionCoreAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct)
    {
        var executionId = ExecutionId.New();
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var inserted = await new ExecutionRepository(connection).InsertAsync(
            executionId.Value,
            id.Value,
            scope.Value,
            outcome.Success,
            evaluation?.Success,
            evaluation?.Score,
            evaluation?.Evaluator,
            outcome.Duration.Ticks,
            outcome.Score,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            ct).ConfigureAwait(false);
        if (inserted == 0)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The Reasonlet does not exist in the scope.");
        }

        return executionId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Feedback replaces the run's earlier scores. A <see cref="ReasonletFeedback.Worked"/> verdict becomes the run's
    /// verdict and overrides the evaluator's and the agent's; feedback with <c>Worked</c> null keeps the stored verdict,
    /// so a verdict can be changed, by recording the other value, but not retracted. When the run's effective success
    /// flips, the Reasonlet's counters follow in the same transaction: <c>successes</c> moves by one, a raised success
    /// moves <c>last_success_at</c> forward to the run's time when that is later, and a lost success that held
    /// <c>last_success_at</c> recomputes it from the remaining successes, or clears it when none remains. Pruned runs
    /// stay in <c>successes</c>, so <c>last_success_at</c> can be null while <c>successes</c> is above zero. Recorded feedback
    /// that gives a <c>Worked</c> verdict tags its <c>mnemosyne.feedback.append</c> activity with
    /// <c>mnemosyne.verdict_source</c> <c>feedback</c>, whether or not it flips the effective success, so a human override
    /// that agrees with the judge shows too; feedback with <c>Worked</c> null, or that fails, is not tagged.
    /// </para>
    /// <para>
    /// One READ COMMITTED transaction of separate statements, so each step sees what committed while an earlier one
    /// waited. It reads the execution's Reasonlet, then locks the Reasonlet <c>FOR KEY SHARE</c>, then the execution
    /// <c>FOR UPDATE</c>, then the Reasonlet's metrics row <c>FOR UPDATE</c>, and only then writes the feedback, the
    /// verdict and the counters. An execution that is missing or in another scope answers
    /// <see cref="MnemosyneErrorCode.NotFound"/>, and so does one that a concurrent erase or prune deletes, with its
    /// Reasonlet or alone, while the feedback waits for it.
    /// </para>
    /// <para>
    /// Every writer takes its locks in the order Reasonlets, then executions, then metrics rows, and feedback takes one
    /// of each, in that order, so it cannot close a cycle of waits:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// While it waits for the Reasonlet it holds nothing. Its <c>FOR KEY SHARE</c> conflicts only with the
    /// <c>FOR UPDATE</c> of an erase or a prune batch, which delete the Reasonlet; appends, remembers and other feedback
    /// pass it.
    /// </description></item>
    /// <item><description>
    /// While it waits for the execution it holds only that key-share lock. The execution's holder is other feedback on
    /// the same run, which holds the same lock; an erase, which locked its Reasonlets first and so does not hold this
    /// one, and takes metrics rows only after all its executions; or a prune batch of old runs, which holds only
    /// executions. A prune batch of Reasonlets locks its Reasonlets <c>SKIP LOCKED</c>, so it never holds one that
    /// feedback holds, nor its executions.
    /// </description></item>
    /// <item><description>
    /// While it waits for the metrics row it holds the Reasonlet and the execution. The row's holder is an append, an
    /// execution-only remember or other feedback, each of which takes the row last and then waits for no other lock;
    /// or a subject erase, which takes metrics rows only once it holds every execution it removes, so it never needs
    /// this one: had it needed it, it would have waited for the feedback before it reached the metrics row. A prune
    /// batch of Reasonlets cannot hold the row, because it skipped the Reasonlet the feedback holds.
    /// </description></item>
    /// </list>
    /// </remarks>
    public ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct) =>
        StoreTelemetry.RunAsync("feedback.append", activity => AppendFeedbackOperationAsync(scope, execution, feedback, activity, ct));

    private async ValueTask<UnitResult<MnemosyneError>> AppendFeedbackOperationAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, Activity? activity, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope);
        if (invalid is null && execution.IsDefault)
        {
            invalid = new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(execution)}: must not be the default id");
        }

        invalid ??= ValidateNested(feedback.Validate(), nameof(feedback));
        if (invalid is { } error)
        {
            return error;
        }

        var recorded = await GuardAsync(AppendFeedbackCoreAsync(scope, execution, feedback, ct)).ConfigureAwait(false);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (feedback.Worked is not null)
        {
            StoreTelemetry.FeedbackVerdictSource(activity);
        }

        return UnitResult<MnemosyneError>.Success();
    }

    private async Task<Result<bool, MnemosyneError>> AppendFeedbackCoreAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();

        // Named rather than left to a default: each statement must see what committed while the one before it waited.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var executions = new ExecutionRepository(connection);
        var id = execution.Value;
        var reasonletId = await executions.GetReasonletOfAsync(id, scope.Value, transaction, ct).ConfigureAwait(false);
        if (reasonletId is not { } reasonlet
            || await executions.LockReasonletForFeedbackAsync(reasonlet, scope.Value, transaction, ct).ConfigureAwait(false) == 0
            || await executions.LockExecutionForFeedbackAsync(id, scope.Value, reasonlet, transaction, ct).ConfigureAwait(false) is not { } before)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The execution does not exist in the scope.");
        }

        await executions.LockMetricsForFeedbackAsync(reasonlet, transaction, ct).ConfigureAwait(false);

        // The execution is held, so the statement updates its row and answers the new effective success.
        var after = (await executions.RecordFeedbackAsync(
            id, scope.Value, feedback.Utility, feedback.Correctness, feedback.Reusability, feedback.Worked, transaction, ct).ConfigureAwait(false))!.Value;
        if (after != before)
        {
            await (after
                ? executions.RaiseSuccessAsync(id, reasonlet, transaction, ct)
                : executions.LoseSuccessAsync(id, reasonlet, transaction, ct)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static MnemosyneError? ValidateScope(ReasonletScope scope) => ValidateNested(scope.Validate(), nameof(scope));

    private static MnemosyneError? ValidateId(ReasonletId id) =>
        id.IsDefault ? new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(id)}: must not be the default id") : null;

    private static MnemosyneError? ValidateNested(UnitResult<MnemosyneError> result, string name) =>
        result.IsFailure ? result.Error with { Message = $"{name}.{result.Error.Message}" } : null;

    /// <inheritdoc />
    /// <remarks>
    /// Runs in one READ COMMITTED transaction with iterative HNSW scans, so a scope that holds a small share of the
    /// table usually still gets up to <paramref name="limit"/> results, sorted exactly by distance; HNSW recall is
    /// approximate. The scan stops at <c>hnsw.max_scan_tuples</c> (20,000 by default), so a very small scope in a very
    /// large table can still come back short. Candidates at exactly equal distance come back in an unspecified
    /// order: a second sort key could stop the index from serving the ordering. The embedding must have a nonzero,
    /// finite norm, or the search is a <see cref="MnemosyneErrorCode.Validation"/> failure.
    /// </remarks>
    public ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct) =>
        StoreTelemetry.RunAsync("search", activity => SearchOperationAsync(scope, embedding, intent, limit, activity, ct));

    private async ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchOperationAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, Activity? activity, CancellationToken ct)
    {
        StoreTelemetry.SearchLimit(activity, limit);
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope) ?? ValidateSearch(embedding.Span, intent, limit);
        if (invalid is not null)
        {
            return invalid.Value;
        }

        var result = await GuardAsync(SearchCoreAsync(scope, embedding, intent, limit, ct)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            StoreTelemetry.Searched(activity, result.Value);
        }

        return result;
    }

    private MnemosyneError? ValidateSearch(ReadOnlySpan<float> embedding, string? intent, int limit)
    {
        if (limit is < 1 or > MaxSearchLimit)
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, $"limit: must be from 1 to {MaxSearchLimit}");
        }

        if (intent is not null && string.IsNullOrWhiteSpace(intent))
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, "intent: must not be blank");
        }

        if (embedding.Length != _options.Dimensions)
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"embedding: must have {_options.Dimensions} values, the store's dimension, but has {embedding.Length}"));
        }

        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
            {
                return new MnemosyneError(MnemosyneErrorCode.Validation, "embedding: must contain only finite numbers");
            }
        }

        return HasCosineNorm(embedding) ? null : NormError("embedding");
    }

    /// <summary>
    /// Whether an embedding has a cosine similarity at all. pgvector sums the squares in single precision, so an
    /// embedding whose squares sum to zero, or overflow to infinity, gives a NaN distance rather than an error.
    /// </summary>
    private static bool HasCosineNorm(ReadOnlySpan<float> embedding)
    {
        var squares = 0f;
        foreach (var value in embedding)
        {
            squares += value * value;
        }

        return squares > 0f && float.IsFinite(squares);
    }

    private static MnemosyneError NormError(string name) =>
        new(MnemosyneErrorCode.Validation, $"{name}: must have a nonzero, finite norm, or cosine similarity is undefined");

    /// <summary>
    /// Makes the HNSW scan keep going until it has <paramref name="limit"/> rows that pass the scope, status and
    /// intent filters, and turns JIT compilation off. Without the iterative scan the index returns about
    /// <c>hnsw.ef_search</c> nearest rows of the whole table and the filters run afterwards, so a scope with a small
    /// share of the rows gets fewer rows than the limit.
    /// </summary>
    /// <remarks>
    /// <c>strict_order</c> rather than <c>relaxed_order</c>, so results stay sorted exactly by distance; HNSW recall
    /// is approximate either way. <c>hnsw.max_scan_tuples</c> stays at its default (20,000), which bounds the work of
    /// a very selective scope. <c>SET</c> takes no bind parameter, so <c>ef_search</c> is an integer that the limit
    /// check already bounded, written with the invariant culture, as the dimension DDL is.
    /// </remarks>
    private static Task ApplySearchSettingsAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, int limit, CancellationToken ct) =>
        SetLocalAsync(
            connection,
            transaction,
            string.Create(
                CultureInfo.InvariantCulture,
                $"SET LOCAL jit = off; SET LOCAL hnsw.iterative_scan = strict_order; SET LOCAL hnsw.ef_search = {Math.Max(limit, DefaultEfSearch)};"),
            ct);

    /// <summary>
    /// Runs <c>SET LOCAL</c> statements, which end with the transaction. The store turns JIT off in its search and
    /// erase and prune transactions: their statements are short, yet a large table can push the planner's cost estimate past
    /// <c>jit_above_cost</c>, and compiling then costs more than the statement saves.
    /// </summary>
    private static async Task SetLocalAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, string settings, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = settings;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchCoreAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await ApplySearchSettingsAsync(connection, transaction, limit, ct).ConfigureAwait(false);
        var rows = await new ReasonletRepository(connection).SearchAsync(scope.Value, ToArray(embedding), intent, limit, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var candidates = new List<ReasonletCandidate>(rows.Count);
        foreach (var row in rows)
        {
            // The parent's counter row has NOT NULL runs, so a null run count means there is no row.
            var parent = row.ParentRuns is { } parentRuns
                ? new ReasonletMetrics(parentRuns, row.ParentSuccesses!.Value, row.ParentLastSuccessAt, row.ParentLastExecutedAt!.Value)
                : (ReasonletMetrics?)null;
            candidates.Add(new ReasonletCandidate(
                ToDefinition(row.ToReasonletRow()),
                row.Similarity,
                new ReasonletMetrics(row.Runs, row.Successes, row.LastSuccessAt, row.LastExecutedAt),
                parent));
        }

        return candidates;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One transaction at READ COMMITTED. An erasure by subject deletes the subject's Reasonlets in the scope with
    /// every execution and feedback on them, other subjects' runs included, then the subject's executions on the
    /// Reasonlets it keeps, subtracting them from those Reasonlets' metrics. It subtracts rather than recounts because the
    /// counters also hold runs whose records retention has pruned. A timestamp the removed runs set falls back to the
    /// latest run left. When none is left, the last run time falls back to the Reasonlet's creation time and the last
    /// success time to null, so no erased run's time stays behind. Other subjects' children of a deleted
    /// Reasonlet survive with a null parent, each the top of its own lineage. The counts are of rows removed, each
    /// once; a cut lineage link is not a removal. The erase covers what exists when it takes its locks: a Reasonlet
    /// remembered while it runs is not erased by it. The Reasonlets are locked deepest first, the order a remember's
    /// path locks take, so an erase waits behind a remember rather than deadlocking with it. Then come the executions
    /// it removes, then the metrics rows it subtracts from, so two overlapping erases take their rows in one order and
    /// complete. Feedback takes the same order, its run's Reasonlet <c>FOR KEY SHARE</c>, the run, then the metrics
    /// row, so an erase and feedback on the same runs both complete, and the erase subtracts each removed run's
    /// effective success, the verdict that counted for it. A concurrent prune never waits for a Reasonlet lock, so it cannot deadlock with an erase. A deadlock
    /// that still happens answers <see cref="MnemosyneErrorCode.Conflict"/>: with a remember that raced the lock walk,
    /// with a hand-written transaction, or with another erase when a third erase cut a lineage between the moment one
    /// of them read its depths and the moment it took its locks, so that the two order the rows they share by
    /// different depths.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="erasure"/> is null.</exception>
    public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct) =>
        StoreTelemetry.RunAsync("erase", activity => EraseOperationAsync(erasure, activity, ct));

    private async ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseOperationAsync(
        ReasonletErasure erasure, Activity? activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(erasure);
        StoreTelemetry.EraseBy(activity, erasure);
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var valid = erasure.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var result = await GuardAsync(EraseCoreAsync(erasure, ct)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            StoreTelemetry.Erased(activity, result.Value);
        }

        return result;
    }

    private async Task<Result<ReasonletErasureResult, MnemosyneError>> EraseCoreAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        var scope = erasure.Scope.Value;
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await SetLocalAsync(connection, transaction, "SET LOCAL jit = off", ct).ConfigureAwait(false);
        var reasonlets = new ReasonletRepository(connection);
        var executions = new ExecutionRepository(connection);

        // Lock first, deepest first, and keep the ids: the later statements take fresh snapshots, and must delete the
        // set that was locked, not rows written since. The locks go Reasonlets, then executions, then the metrics rows
        // of the kept Reasonlets that lose runs, each in a fixed order, so overlapping erases meet on the same rows in
        // the same order and cannot deadlock. The executions go before the Reasonlets, so that none is left to cascade
        // uncounted. A scope erase keeps no Reasonlet, so it locks and deletes the doomed Reasonlets' executions in one
        // statement and has no metrics row to subtract from. A subject erase keeps the executions it locked in a
        // temporary table, so the set stays on the server, and its delete takes its snapshot after the metrics locks,
        // so it sees every append that committed before.
        var doomed = await reasonlets.LockForErasureAsync(scope, erasure.SubjectId, transaction, ct).ConfigureAwait(false);
        int? erased;
        if (erasure.SubjectId is not { } subjectId)
        {
            erased = await executions.DeleteScopeExecutionsAsync(scope, doomed, transaction, ct).ConfigureAwait(false);
        }
        else
        {
            await executions.CreateErasedExecutionsAsync(transaction, ct).ConfigureAwait(false);
            await executions.LockExecutionsForErasureAsync(scope, doomed, subjectId, transaction, ct).ConfigureAwait(false);
            var kept = await executions.LockMetricsForErasureAsync(scope, doomed, transaction, ct).ConfigureAwait(false);
            erased = await executions.DeleteForErasureAsync(scope, kept, transaction, ct).ConfigureAwait(false);
        }

        var removed = await reasonlets.DeleteAsync(scope, doomed, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new ReasonletErasureResult { Reasonlets = removed, Executions = erased!.Value };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The prune reads the database's clock once and measures each window back from it, so a long prune has a fixed
    /// target and ends. Executions recorded before <c>now - </c><see cref="ReasonletRetention.ExecutionsOlderThan"/> are
    /// deleted, with their feedback, in batches of at most 1,000, each its own READ COMMITTED transaction, until a
    /// batch deletes nothing; the Reasonlets' counters keep them, with the verdict that counted for each, because a
    /// pruned run still happened. A batch locks its executions in id order, as an erase does, and takes no metrics
    /// lock, so it never waits for an append. It can wait for feedback that holds one of its runs, but that feedback
    /// waits, if at all, only for a metrics row, which the batch never holds, so the batch then deletes the run with
    /// the feedback's verdict counted.
    /// </para>
    /// <para>
    /// Then Reasonlets that are idle against <c>now - </c><see cref="ReasonletRetention.ReasonletsUnusedFor"/> are
    /// deleted, with their runs, feedback and metrics. Use is judged per lineage: a Reasonlet is used when it or any
    /// version below it was remembered or last ran at or after the cutoff, so a superseded version stays while a later
    /// one is in use, and a lineage goes only once its whole subtree is idle. <see cref="ReasonletStatus.PendingReview"/>
    /// Reasonlets are judged like active ones. The idle ones are listed once, deepest first, and deleted in batches of
    /// at most 1,000 in that order, so a whole idle lineage goes in one prune; a batch keeps any Reasonlet with a
    /// descendant it does not hold, so it never deletes a Reasonlet and keeps its child. A batch deletes its
    /// Reasonlets' runs with them, unless they hold more than 1,000, which then go first in batches of their own: no
    /// transaction deletes more than 1,000 rows of either kind directly, and the metrics and feedback rows that
    /// cascade come on top. Each batch locks its Reasonlets without waiting, skipping any that another transaction
    /// holds, then their runs, then their metrics rows, and re-checks before it deletes: a Reasonlet that ran, or
    /// gained a version, after the list was read is kept and not counted, and so are its ancestors, and a skipped
    /// Reasonlet keeps its ancestors too, until a later prune. An append or a remember that arrives after the locks
    /// waits, then answers <see cref="MnemosyneErrorCode.NotFound"/>, as after an erase. A Reasonlet that becomes idle
    /// while the prune runs, because an erase removed the version that kept it, waits for the next prune.
    /// </para>
    /// <para>
    /// A batch that hits a deadlock or a serialization failure rolls back and is retried after a short jittered wait,
    /// up to three times, before the prune gives up with <see cref="MnemosyneErrorCode.Conflict"/>. Any other failure ends the prune at once. On
    /// a failure the batches committed before it stay committed and the result carries no counts; running the prune
    /// again deletes what is still older than its new cutoff.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="retention"/> is null.</exception>
    public ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(ReasonletRetention retention, CancellationToken ct) =>
        PruneAsync(retention, new PruneProgress(), ct);

    /// <summary>The batch size a prune uses: <see cref="PruneBatchSize"/>, unless a test sets a smaller one.</summary>
    internal int PruneBatchSizeOverride { get; set; } = PruneBatchSize;

    /// <summary>
    /// A test hook that each prune batch awaits inside its transaction, before its first statement, so a test can
    /// commit a change from another transaction that the batch's locks and re-check must then see.
    /// </summary>
    internal Func<Task>? BeforePruneBatch { get; set; }

    /// <summary>
    /// A test hook that each prune batch awaits inside its transaction, after its statements and before the commit, so
    /// a test can make a batch fail and roll back.
    /// </summary>
    internal Func<Task>? BeforePruneBatchCommit { get; set; }

    /// <summary>
    /// A test hook that each prune batch awaits after its commit, so a test can check what a committed batch left.
    /// </summary>
    internal Func<Task>? AfterPruneBatch { get; set; }

    /// <summary>
    /// Waits out the backoff before a prune batch is tried again: <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
    /// unless a test replaces it, so the retry tests need not sleep.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> PruneRetryDelay { get; set; } = Task.Delay;

    /// <summary>
    /// Prunes as <see cref="PruneAsync(ReasonletRetention, CancellationToken)"/> does, and adds every committed batch to
    /// <paramref name="progress"/>, which therefore holds what was deleted even when the prune fails. The telemetry
    /// reads its counts from there, so it counts every committed batch however the prune ends.
    /// </summary>
    internal ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(
        ReasonletRetention retention, PruneProgress progress, CancellationToken ct) =>
        StoreTelemetry.RunAsync("prune", activity => PruneOperationAsync(retention, progress, activity, ct));

    private async ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneOperationAsync(
        ReasonletRetention retention, PruneProgress progress, Activity? activity, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(retention);
            var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
            if (migrated.IsFailure)
            {
                return migrated.Error;
            }

            var valid = retention.Validate();
            if (valid.IsFailure)
            {
                return valid.Error;
            }

            return await GuardAsync(PruneCoreAsync(retention, progress, ct)).ConfigureAwait(false);
        }
        finally
        {
            StoreTelemetry.Pruned(activity, progress);
        }
    }

    private async Task<Result<ReasonletPruneResult, MnemosyneError>> PruneCoreAsync(
        ReasonletRetention retention, PruneProgress progress, CancellationToken ct)
    {
        var scope = retention.Scope?.Value;
        var batchSize = PruneBatchSizeOverride;
        var now = await ReadNowAsync(ct).ConfigureAwait(false);

        // Run records go first, so that the idle-lineage pass that follows counts only runs this pass left.
        if (retention.ExecutionsOlderThan is { } window)
        {
            var cutoff = Cutoff(now, window);

            // Until a batch deletes nothing, not until one comes back short: a batch skips the runs a concurrent erase
            // deleted while it waited for them, so a short batch does not prove that no old run is left.
            int deleted;
            do
            {
                deleted = await RunPruneBatchAsync(
                    (connection, transaction) => scope is null
                        ? new RetentionRepository(connection).DeleteOldExecutionsAsync(cutoff, batchSize, transaction, ct)
                        : new RetentionRepository(connection).DeleteOldExecutionsInScopeAsync(scope, cutoff, batchSize, transaction, ct),
                    ct).ConfigureAwait(false);
                progress.Executions += deleted;
            }
            while (deleted > 0);
        }

        if (retention.ReasonletsUnusedFor is { } unused)
        {
            await PruneIdleReasonletsAsync(scope, Cutoff(now, unused), batchSize, progress, ct).ConfigureAwait(false);
        }

        return new ReasonletPruneResult { Reasonlets = progress.Reasonlets, Executions = progress.Executions };
    }

    /// <summary>
    /// Deletes the Reasonlets of the scope, or of every scope, that are idle against <paramref name="cutoff"/>, with
    /// their runs, and adds every committed batch to <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idle ids are listed once, deepest first, and taken a chunk of <paramref name="batchSize"/> at a time. The
    /// order lets a whole idle subtree go in one prune: a row's children are in its chunk or went in an earlier one,
    /// so they are gone or held when its batch runs. What keeps a batch from deleting a parent without its children
    /// is the re-check, which keeps every row with a descendant the batch does not hold. The list can hold too much: a row
    /// that became used since it was read is dropped by its batch's re-check. It can also hold too little: a row an
    /// erase made idle since, by removing the descendant that kept it, waits for the next prune.
    /// </para>
    /// <para>
    /// A chunk normally goes in one Reasonlet batch, which deletes its idle Reasonlets with their runs. A batch first
    /// counts the runs of the Reasonlets it holds, without locking them. When there are more than
    /// <paramref name="batchSize"/>, it deletes a batch of them instead, more run batches follow until one comes back
    /// short, and then the Reasonlet batch. A Reasonlet batch
    /// still checks the bound on the rows it finds idle, and when they hold too many runs it deletes a batch of them
    /// and leaves the Reasonlets for the next batch, so no transaction deletes more than <paramref name="batchSize"/>
    /// rows of either kind directly.
    /// </para>
    /// <para>
    /// Each batch is its own transaction. It locks the chunk's Reasonlets <c>FOR UPDATE SKIP LOCKED</c>, so it never
    /// waits for a Reasonlet lock, and its re-check treats a row it does not hold as used, so a skipped row keeps
    /// its whole ancestry until a later prune. A Reasonlet batch then locks the held rows' executions in id order,
    /// then their metrics rows in id order, and only then re-checks and deletes, touching no row it does not hold
    /// but the feedback its deletes cascade to. A run batch locks the held rows, then deletes executions it locks in
    /// id order; it takes no metrics lock. So a batch waits only for an execution, a metrics row or a feedback row,
    /// and a cycle of waits would need another writer to wait for the batch while it holds one of those. None does:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An append holds its Reasonlet <c>FOR KEY SHARE</c> from its first step, so a batch that comes later skips the
    /// Reasonlet and never locks its runs or metrics row; the append's new run is not visible to it. An append that
    /// comes later waits at the Reasonlet the batch holds, holding nothing, and then answers
    /// <see cref="MnemosyneErrorCode.NotFound"/>.
    /// </description></item>
    /// <item><description>
    /// Feedback holds its Reasonlet <c>FOR KEY SHARE</c> from its first lock, before its execution and metrics row, so
    /// a batch that comes later skips the Reasonlet and never locks that execution or metrics row. Feedback that comes
    /// later waits at the Reasonlet the batch holds, holding nothing, and then answers
    /// <see cref="MnemosyneErrorCode.NotFound"/>.
    /// </description></item>
    /// <item><description>
    /// A remember waits only for Reasonlets, and holds Reasonlets on its path and the rows it inserts. The batch skips
    /// the held path rows, so it locks neither their runs nor their metrics, and cannot see the inserted rows.
    /// </description></item>
    /// <item><description>
    /// An erase locks Reasonlets, then executions in id order, then the metrics rows of the Reasonlets it keeps in id
    /// order. While it waits for a Reasonlet it holds no execution or metrics row. While it waits for an execution the
    /// batch holds, it holds executions of lower ids and no metrics row, and the batch, taking executions in id order
    /// too, cannot be waiting for one of them. While it waits for a metrics row the batch holds, the batch has all its
    /// executions and waits, if at all, for a metrics row of a higher id, which the erase does not hold yet, or for a
    /// feedback row of its own executions, which the erase does not touch.
    /// </description></item>
    /// <item><description>
    /// Two prune batches hold disjoint Reasonlets, so they lock disjoint executions and metrics rows. A batch of old
    /// runs holds no Reasonlet and no metrics row, and takes executions in id order, as every batch does.
    /// </description></item>
    /// </list>
    /// </remarks>
    private async Task PruneIdleReasonletsAsync(string? scope, DateTime cutoff, int batchSize, PruneProgress progress, CancellationToken ct)
    {
        var idle = await ListIdleReasonletsAsync(scope, cutoff, ct).ConfigureAwait(false);
        foreach (var chunk in idle.Chunk(batchSize))
        {
            // Whether a batch that counts more runs than the bound strips them first. Once they have gone, the runs left
            // belong to rows the re-check keeps, and the bounded delete handles the rare rest.
            var stripRuns = true;
            IdleBatch batch;
            do
            {
                var strip = stripRuns;
                batch = await RunPruneBatchAsync(
                    async (connection, transaction) =>
                    {
                        var retention = new RetentionRepository(connection);
                        var locked = await retention.LockIdleReasonletsAsync(chunk, transaction, ct).ConfigureAwait(false);
                        if (strip && await retention.CountIdleExecutionsAsync(locked, transaction, ct).ConfigureAwait(false) > batchSize)
                        {
                            // Too many runs to delete with the Reasonlets: this batch deletes one batch of them instead.
                            var stripped = await retention.DeleteIdleExecutionsAsync(locked, cutoff, batchSize, transaction, ct).ConfigureAwait(false);
                            return new IdleBatch(0, stripped, StrippedRunsFirst: true, ChunkDone: false);
                        }

                        await retention.LockIdleExecutionsAsync(locked, transaction, ct).ConfigureAwait(false);
                        await retention.LockIdleMetricsAsync(locked, transaction, ct).ConfigureAwait(false);

                        // An aggregate always answers a row, so the null-forgiving operator only skips a check the query cannot fail.
                        var deleted = (await retention.DeleteStillIdleAsync(locked, cutoff, batchSize, transaction, ct).ConfigureAwait(false))!;
                        return new IdleBatch(deleted.Reasonlets, deleted.Executions, StrippedRunsFirst: false, ChunkDone: !deleted.Stripped);
                    },
                    ct).ConfigureAwait(false);
                progress.Reasonlets += batch.Reasonlets;
                progress.Executions += batch.Executions;
                if (batch.StrippedRunsFirst)
                {
                    // A full batch means more may be left; a short one means the idle rows' runs are gone.
                    if (batch.Executions == batchSize)
                    {
                        await StripIdleRunsAsync(chunk, cutoff, batchSize, progress, ct).ConfigureAwait(false);
                    }

                    stripRuns = false;
                }
            }
            while (!batch.ChunkDone);
        }
    }

    /// <summary>What one batch of a chunk did.</summary>
    /// <param name="Reasonlets">The Reasonlets it deleted.</param>
    /// <param name="Executions">The executions it deleted.</param>
    /// <param name="StrippedRunsFirst">
    /// Whether it counted more runs than the bound and deleted a batch of them instead of the Reasonlets.
    /// </param>
    /// <param name="ChunkDone">Whether the chunk's idle Reasonlets are gone, so no further batch is needed.</param>
    private readonly record struct IdleBatch(int Reasonlets, int Executions, bool StrippedRunsFirst, bool ChunkDone);

    /// <summary>
    /// Deletes the runs of a chunk's idle Reasonlets in run batches of at most <paramref name="batchSize"/>, until one
    /// comes back short, and adds each to <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// A short batch ends the loop even when a concurrent erase made it short while runs remain, because the
    /// Reasonlet batch that follows checks the bound itself and deletes what is left a batch at a time.
    /// </remarks>
    private async Task StripIdleRunsAsync(Guid[] chunk, DateTime cutoff, int batchSize, PruneProgress progress, CancellationToken ct)
    {
        int deleted;
        do
        {
            deleted = await RunPruneBatchAsync(
                async (connection, transaction) =>
                {
                    var retention = new RetentionRepository(connection);
                    var locked = await retention.LockIdleReasonletsAsync(chunk, transaction, ct).ConfigureAwait(false);
                    return await retention.DeleteIdleExecutionsAsync(locked, cutoff, batchSize, transaction, ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            progress.Executions += deleted;
        }
        while (deleted == batchSize);
    }

    /// <summary>
    /// Lists the idle Reasonlets of the scope, or of every scope, deepest first, in a transaction of its own with JIT
    /// off. The list takes no lock, so nothing waits for it.
    /// </summary>
    private async Task<Guid[]> ListIdleReasonletsAsync(string? scope, DateTime cutoff, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await SetLocalAsync(connection, transaction, "SET LOCAL jit = off", ct).ConfigureAwait(false);
        var retention = new RetentionRepository(connection);
        var idle = scope is null
            ? await retention.ListIdleReasonletsAsync(cutoff, transaction, ct).ConfigureAwait(false)
            : await retention.ListIdleReasonletsInScopeAsync(scope, cutoff, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return idle;
    }

    private async Task<DateTime> ReadNowAsync(CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();

        // SELECT now() always answers a row, so the null-forgiving operator only skips a check the query cannot fail.
        return (await new RetentionRepository(connection).NowAsync(ct).ConfigureAwait(false))!.Value;
    }

    /// <summary>
    /// Answers <paramref name="now"/> less <paramref name="window"/>. A window that reaches back past the first
    /// representable instant answers that instant, before every stored row, so the window deletes nothing rather
    /// than throwing.
    /// </summary>
    private static DateTime Cutoff(DateTime now, TimeSpan window) =>
        window < now - DateTime.MinValue ? now - window : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Runs one prune batch in its own READ COMMITTED transaction, with JIT off as in an erase, and commits it. A
    /// batch that fails with a deadlock (<c>40P01</c>) or a serialization failure (<c>40001</c>) has rolled back, so it
    /// is run again from the start, up to <see cref="PruneRetries"/> times, after a backoff from
    /// <see cref="PruneRetryBackoff"/>; the last such failure, and any other, propagates for
    /// <see cref="GuardAsync{T}"/> to map.
    /// </summary>
    /// <remarks>
    /// The batch receives the connection as well as the transaction, because the repositories are bound to a
    /// connection. Every try opens a fresh connection, so a retry never runs on a connection the failure left in an
    /// unknown state. The backoff gives the transaction the batch collided with time to finish, so the retry does not
    /// meet it again at once, and its jitter keeps two prunes that collided from retrying in step.
    /// </remarks>
    private async Task<T> RunPruneBatchAsync<T>(Func<IAsyncDbConnection, IAsyncDbTransaction, Task<T>> batch, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A flag rather than a return inside the try, so every line of the method can execute.
            var retry = false;
            T result = default!;
            try
            {
                result = await RunPruneBatchOnceAsync(batch, ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
                && attempt <= PruneRetries)
            {
                // The transaction rolled back when it was disposed, so the next try starts over.
                retry = true;
            }

            if (!retry)
            {
                return result;
            }

            await PruneRetryDelay(PruneRetryBackoff(attempt), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The wait before the <paramref name="attempt"/>th retry of a prune batch: 50 ms times the attempt, give or take
    /// half, drawn from <see cref="Random.Shared"/>.
    /// </summary>
    internal static TimeSpan PruneRetryBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(50 * attempt * (0.5 + Random.Shared.NextDouble()));

    private async Task<T> RunPruneBatchOnceAsync<T>(Func<IAsyncDbConnection, IAsyncDbTransaction, Task<T>> batch, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await SetLocalAsync(connection, transaction, "SET LOCAL jit = off", ct).ConfigureAwait(false);
        if (BeforePruneBatch is { } before)
        {
            await before().ConfigureAwait(false);
        }

        var result = await batch(connection, transaction).ConfigureAwait(false);
        if (BeforePruneBatchCommit is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        // Not cancellable: once the commit is sent, cancelling it could leave the server committed while this side
        // throws, and the batch would then be missing from the progress that the result and the telemetry count.
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        if (AfterPruneBatch is { } after)
        {
            await after().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Succeeds once the schema is migrated to <see cref="SchemaVersion"/> with this store's dimension, and caches
    /// that. Answers <see cref="MnemosyneErrorCode.Unavailable"/> before <see cref="MigrateAsync"/> has run, and on a
    /// database that an older release migrated, which needs <see cref="MigrateAsync"/> again: a statement would
    /// otherwise fail on a missing column. Answers <see cref="MnemosyneErrorCode.Validation"/> when the recorded
    /// dimension differs from the configured one.
    /// </summary>
    internal async ValueTask<UnitResult<MnemosyneError>> EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        UnitResult<MnemosyneError> result;
        try
        {
            result = await CheckMigratedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    private async Task<UnitResult<MnemosyneError>> CheckMigratedAsync(CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var settings = new SettingsRepository(connection);
        var recorded = await settings.SettingsTableExistsAsync(ct).ConfigureAwait(false)
            ? await settings.GetDimensionsAsync(ct).ConfigureAwait(false)
            : null;
        if (recorded is not { } dimensions || await settings.GetSchemaVersionAsync(ct).ConfigureAwait(false) is not >= SchemaVersion)
        {
            return new MnemosyneError(MnemosyneErrorCode.Unavailable, "schema not migrated: call MigrateAsync");
        }

        if (dimensions != _options.Dimensions)
        {
            return DimensionMismatch(dimensions);
        }

        _migrated = true;
        return UnitResult<MnemosyneError>.Success();
    }

    // DDL takes no parameters, so these statements are raw text. Only the dimension and the HNSW
    // parameters are interpolated: integers that MnemosynePostgreSqlOptions.Validate has range-checked,
    // formatted with the invariant culture so no culture can change the digits.
    private async Task ApplyDimensionsAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"""
                ALTER TABLE mnemosyne.reasonlets ALTER COLUMN embedding TYPE vector({_options.Dimensions});
                CREATE INDEX reasonlets_embedding_hnsw ON mnemosyne.reasonlets
                    USING hnsw (embedding vector_cosine_ops)
                    WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction});
                """);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Maps the database failures that <see cref="PostgresErrors"/> knows to an error; anything else propagates.</summary>
    private static async Task<Result<T, MnemosyneError>> GuardAsync<T>(Task<Result<T, MnemosyneError>> work)
    {
        // Assigned rather than returned inside the try, so every line of the method can execute.
        Result<T, MnemosyneError> result;
        try
        {
            result = await work.ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    /// <summary>Unwraps a result that only a bug in the store can fail, and throws when it does.</summary>
    private static T OrThrow<T>(Result<T, MnemosyneError> result, string what) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"{what} failed: {result.Error.Message}");

    private static string? SerializeTags(IReadOnlyDictionary<string, string>? tags) =>
        tags is null ? null : JsonSerializer.Serialize(tags, TagsJson.Default.IReadOnlyDictionaryStringString);

    /// <summary>Returns the embedding's own array when the memory spans a whole array, and a copy otherwise.</summary>
    private static float[] ToArray(ReadOnlyMemory<float> embedding) =>
        MemoryMarshal.TryGetArray(embedding, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : embedding.ToArray();

    private static MnemosyneError LineageConflict() =>
        new(MnemosyneErrorCode.Conflict, "The lineage changed under a concurrent erase.");

    private MnemosyneError HnswMismatch(SettingsRow recorded) => new(
        MnemosyneErrorCode.Validation,
        string.Create(
            CultureInfo.InvariantCulture,
            $"HnswM and HnswEfConstruction: the database's HNSW index was built with m = {recorded.HnswM} and ef_construction = {recorded.HnswEfConstruction}, but the store is configured with m = {_options.HnswM} and ef_construction = {_options.HnswEfConstruction}. The options apply only when the first MigrateAsync builds the index."));

    private MnemosyneError DimensionMismatch(int recorded) => new(
        MnemosyneErrorCode.Validation,
        string.Create(
            CultureInfo.InvariantCulture,
            $"Dimensions: the database's embeddings have {recorded} dimensions, but the store is configured with {_options.Dimensions}. Mixing embedding models would corrupt similarity."));
}
