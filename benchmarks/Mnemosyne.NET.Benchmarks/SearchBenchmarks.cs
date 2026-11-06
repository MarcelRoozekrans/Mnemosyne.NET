using BenchmarkDotNet.Attributes;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;

namespace Mnemosyne.NET.Benchmarks;

/// <summary>
/// The PostgreSQL similarity search over 1000 stored Reasonlets, with the lineage id resolved for each hit. It needs a
/// pgvector database, whose connection string is in <see cref="ConnectionVariable"/>.
/// </summary>
[MemoryDiagnoser]
public class SearchBenchmarks
{
    /// <summary>The environment variable that holds the connection string of a pgvector database.</summary>
    internal const string ConnectionVariable = "MNEMOSYNE_BENCH_PG";

    private const int Dimensions = 256;
    private const int Entries = 1000;
    private const int ChainLength = 20;

    private NpgsqlDataSource _dataSource = null!;
    private ReasonletPostgreSqlStore _store = null!;
    private ReasonletScope _scope;
    private float[] _query = null!;

    /// <summary>Gets or sets the length of each version chain: 1 stores roots only, 20 stores chains of 20 versions.</summary>
    [Params(1, 20)]
    public int LineageDepth { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException($"{ConnectionVariable} is not set.");
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        _store = new ReasonletPostgreSqlStore(_dataSource, new MnemosynePostgreSqlOptions { Dimensions = Dimensions });
        _scope = new ReasonletScope($"bench-{LineageDepth}-{Guid.NewGuid():N}");
        _query = BenchmarkData.Embedding(int.MaxValue, Dimensions);

        var migrated = await _store.MigrateAsync(CancellationToken.None);
        if (migrated.IsFailure)
        {
            throw new InvalidOperationException($"Migration failed: {migrated.Error.Message}");
        }

        ReasonletId? parent = null;
        for (var i = 0; i < Entries; i++)
        {
            // Each version has its own graph, or the store would record an execution instead of a new Reasonlet.
            parent = i % LineageDepth == 0 ? null : parent;
            var receipt = await _store.RememberAsync(Entry(i, parent), CancellationToken.None);
            if (receipt.IsFailure)
            {
                throw new InvalidOperationException($"Seeding entry {i} failed: {receipt.Error.Message}");
            }

            parent = receipt.Value.Id;
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _store.EraseAsync(new ReasonletErasure(_scope, null), CancellationToken.None);
        await _dataSource.DisposeAsync();
    }

    [Benchmark]
    public async Task<int> SearchAsync()
    {
        var result = await _store.SearchAsync(_scope, _query, null, 10, CancellationToken.None);
        return result.IsSuccess ? result.Value.Count : throw new InvalidOperationException(result.Error.Message);
    }

    private ReasonletStoreEntry Entry(int index, ReasonletId? parent) => new(
        new ReasonletCapture
        {
            Scope = _scope,
            Question = $"How do I run procedure {index}?",
            Graph = new ReasonletGraph
            {
                Nodes = [new ReasonletNode { Id = "n1", Type = "step", Name = $"step-{index}" }, new ReasonletNode { Id = "n2", Type = "answer" }],
                Edges = [new ReasonletEdge { From = "n1", To = "n2" }],
                SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
            },
            Outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(100), 0.8),
            Provenance = new ReasonletProvenance { Source = "benchmark" },
            ParentId = parent,
        },
        "benchmark",
        $"procedure-{index}",
        BenchmarkData.Embedding(index, Dimensions));
}
