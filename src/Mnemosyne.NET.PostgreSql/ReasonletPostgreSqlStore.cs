using System.Data;
using System.Data.Async;
using System.Data.Async.Adapters;
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
/// <see cref="MnemosyneErrorCode.Unavailable"/> until the schema is migrated. The store opens one connection
/// per operation from the data source it is given.
/// </remarks>
public sealed class ReasonletPostgreSqlStore : IReasonletStore
{
    private const int MaxSearchLimit = 200;
    private const int DefaultEfSearch = 40;
    private const string MigrationResourcePrefix = "Mnemosyne.NET.PostgreSql.Migrations.";

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
    public async ValueTask<UnitResult<MnemosyneError>> MigrateAsync(CancellationToken ct)
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
    /// the path to the top until commit. An execution only locks the parent alone. A new version that loses a race
    /// with a concurrent erase of its lineage answers <see cref="MnemosyneErrorCode.Conflict"/>.
    /// </para>
    /// <para>
    /// The embedding must have <see cref="MnemosynePostgreSqlOptions.Dimensions"/> values, or the entry is a
    /// <see cref="MnemosyneErrorCode.Validation"/> failure.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The stored lineage is corrupt: the <c>parent_id</c> links above the parent form a cycle, which only
    /// hand-written SQL can create.
    /// </exception>
    public async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        ArgumentNullException.ThrowIfNull(entry);
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

        return await GuardAsync(RememberCoreAsync(entry, ct)).ConfigureAwait(false);
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
                var execution = await InsertExecutionAsync(executions, parent.Id, capture, transaction, ct).ConfigureAwait(false);
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
        var execution = await InsertExecutionAsync(executions, id.Value, capture, transaction, ct).ConfigureAwait(false);
        return new ReasonletReceipt(id, version, execution, kind, ReasonletStatus.Active);
    }

    /// <summary>Inserts the capture's execution of a Reasonlet this transaction holds or has just inserted, and counts it in the Reasonlet's metrics.</summary>
    private static async Task<ExecutionId> InsertExecutionAsync(
        ExecutionRepository executions, Guid reasonletId, ReasonletCapture capture, IAsyncDbTransaction transaction, CancellationToken ct)
    {
        var id = ExecutionId.New();
        var outcome = capture.Outcome;
        var provenance = capture.Provenance;
        await executions.InsertAsync(
            id.Value,
            reasonletId,
            capture.Scope.Value,
            outcome.Success,
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
    public async ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
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
    /// prior read. The statement takes the Reasonlet <c>FOR KEY SHARE</c>, which a remember's locks let through, so an
    /// append never waits for a remember in the same lineage; it waits only for an erase that holds the Reasonlet,
    /// and a Reasonlet that erase deletes answers <see cref="MnemosyneErrorCode.NotFound"/>.
    /// </remarks>
    public async ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope) ?? ValidateId(id) ?? ValidateNested(outcome.Validate(), nameof(outcome)) ?? ValidateNested(provenance.Validate(), nameof(provenance));
        if (invalid is { } error)
        {
            return error;
        }

        return await GuardAsync(AppendExecutionCoreAsync(scope, id, outcome, provenance, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ExecutionId, MnemosyneError>> AppendExecutionCoreAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        var executionId = ExecutionId.New();
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var inserted = await new ExecutionRepository(connection).InsertAsync(
            executionId.Value,
            id.Value,
            scope.Value,
            outcome.Success,
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
    /// One scoped upsert keyed on the execution, so feedback recorded again replaces the earlier feedback. An
    /// execution that is missing or in another scope answers <see cref="MnemosyneErrorCode.NotFound"/>. An execution
    /// that a concurrent erase deletes while the upsert waits for it answers <see cref="MnemosyneErrorCode.Conflict"/>:
    /// the foreign-key check finds the execution gone.
    /// </remarks>
    public async ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
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
        return recorded.IsFailure ? recorded.Error : UnitResult<MnemosyneError>.Success();
    }

    private async Task<Result<bool, MnemosyneError>> AppendFeedbackCoreAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var written = await new ExecutionRepository(connection).UpsertFeedbackAsync(
            execution.Value, scope.Value, feedback.Utility, feedback.Correctness, feedback.Reusability, ct).ConfigureAwait(false);
        if (written == 0)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The execution does not exist in the scope.");
        }

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
    /// order: a second sort key could stop the index from serving the ordering.
    /// </remarks>
    public async ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
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

        return await GuardAsync(SearchCoreAsync(scope, embedding, intent, limit, ct)).ConfigureAwait(false);
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

        return null;
    }

    /// <summary>
    /// Makes the HNSW scan keep going until it has <paramref name="limit"/> rows that pass the scope, status and
    /// intent filters. Without it the index returns about <c>hnsw.ef_search</c> nearest rows of the whole table
    /// and the filters run afterwards, so a scope with a small share of the rows gets fewer rows than the limit.
    /// </summary>
    /// <remarks>
    /// <c>strict_order</c> rather than <c>relaxed_order</c>, so results stay sorted exactly by distance; HNSW recall
    /// is approximate either way. <c>hnsw.max_scan_tuples</c> stays at its default (20,000), which bounds the work of
    /// a very selective scope. <c>SET</c> takes no bind parameter, so <c>ef_search</c> is an integer that the limit
    /// check already bounded, written with the invariant culture, as the dimension DDL is. <c>SET LOCAL</c> ends with
    /// the transaction.
    /// </remarks>
    private static async Task ApplyIterativeScanAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, int limit, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"SET LOCAL hnsw.iterative_scan = strict_order; SET LOCAL hnsw.ef_search = {Math.Max(limit, DefaultEfSearch)};");
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchCoreAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await ApplyIterativeScanAsync(connection, transaction, limit, ct).ConfigureAwait(false);
        var rows = await new ReasonletRepository(connection).SearchAsync(scope.Value, ToArray(embedding), intent, limit, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        var candidates = new List<ReasonletCandidate>(rows.Count);
        foreach (var row in rows)
        {
            candidates.Add(new ReasonletCandidate(ToDefinition(row.ToReasonletRow()), row.Similarity, new ReasonletMetrics(row.Runs, row.Successes, row.LastSuccessAt)));
        }

        return candidates;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One transaction at READ COMMITTED. An erasure by subject deletes the subject's Reasonlets in the scope with
    /// every execution and feedback on them, other subjects' runs included, then the subject's executions on the
    /// Reasonlets it keeps, whose metrics it recounts from the executions left. Other subjects' children of a deleted
    /// Reasonlet survive with a null parent, each the top of its own lineage. The counts are of rows removed, each
    /// once; a cut lineage link is not a removal. The erase covers what exists when it takes its locks: a Reasonlet
    /// remembered while it runs is not erased by it. The rows are locked deepest first, the order a remember's path
    /// locks take, so an erase waits behind a remember rather than deadlocking with it; a deadlock that still
    /// happens, with a remember that raced the lock walk or with a hand-written transaction, answers
    /// <see cref="MnemosyneErrorCode.Conflict"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="erasure"/> is null.</exception>
    public async ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        ArgumentNullException.ThrowIfNull(erasure);
        var valid = erasure.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        return await GuardAsync(EraseCoreAsync(erasure, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ReasonletErasureResult, MnemosyneError>> EraseCoreAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        var scope = erasure.Scope.Value;
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var reasonlets = new ReasonletRepository(connection);
        var executions = new ExecutionRepository(connection);

        // Lock first, deepest first, and keep the ids: the later statements take fresh snapshots, and must delete the
        // set that was locked, not Reasonlets remembered since. The executions go before the Reasonlets, so that none
        // is left to cascade uncounted. The locks go Reasonlets, then executions, then metrics rows, each in a fixed
        // order, and so do the cascades of the final delete.
        var doomed = await reasonlets.LockForErasureAsync(scope, erasure.SubjectId, transaction, ct).ConfigureAwait(false);
        var erased = (await executions.DeleteForErasureAsync(scope, doomed, erasure.SubjectId, transaction, ct).ConfigureAwait(false))!;
        if (erased.Recount.Length > 0)
        {
            await executions.LockMetricsAsync(scope, erased.Recount, transaction, ct).ConfigureAwait(false);
            await executions.RecountMetricsAsync(scope, erased.Recount, transaction, ct).ConfigureAwait(false);
        }

        var removed = await reasonlets.DeleteAsync(scope, doomed, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new ReasonletErasureResult { Reasonlets = removed, Executions = erased.Count };
    }

    /// <summary>
    /// Succeeds once the schema is migrated with this store's dimension, and caches that. Answers
    /// <see cref="MnemosyneErrorCode.Unavailable"/> before <see cref="MigrateAsync"/> has run, and
    /// <see cref="MnemosyneErrorCode.Validation"/> when the recorded dimension differs from the configured one.
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
        if (recorded is not { } dimensions)
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
