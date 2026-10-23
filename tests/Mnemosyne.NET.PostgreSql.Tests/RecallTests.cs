using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// The <see cref="ReasonletMemory"/> facade over the real PostgreSQL store, with the default selector and options, and an
/// embedding generator that maps each known text to a fixed unit vector.
/// </summary>
public sealed class RecallTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const double Tolerance = 1e-6;

    // Every Reasonlet in these tests is remembered and run within seconds of the recall, against a 30-day half-life, so
    // its freshness is 0.5 ^ (age / 30 days), above 0.99999. Clock skew can only push it to 1, since a negative age counts
    // as zero.
    private const double FreshFloor = 0.99999;

    private const string Query = "How do I export the monthly report?";
    private const string Original = "Export the monthly report to PDF";
    private const string NewVersion = "Export the monthly report to PDF, faster";
    private const string CloseMatch = "Export the weekly report";
    private const string FarAway = "Water the office plants";

    private static readonly ReasonletOutcome Success = new(true, TimeSpan.FromMilliseconds(10), null);
    private static readonly ReasonletOutcome Failure = new(false, TimeSpan.FromMilliseconds(10), null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecallRanksTheBrainstormExample()
    {
        // The query embeds to (1, 0, 0) and each Reasonlet's question to (s, sqrt(1 - s^2), 0), a unit vector whose cosine
        // with the query is s. So the similarities are 0.85 for the original, 0.86 for the new version and 0.92 for the
        // close match, as in the selector's unit test of the same example.
        var embeddings = new FixedEmbeddings
        {
            [Query] = [1f, 0f, 0f],
            [Original] = Unit(0.85),
            [NewVersion] = Unit(0.86),
            [CloseMatch] = Unit(0.92),
        };
        var (memory, scope) = await MemoryAsync(embeddings);

        // The original: its remember run and 39 more successes make 40 out of 40.
        var original = await RememberAsync(memory, scope, Original, "original");
        await RunAsync(memory, scope, original, Success, 39);

        // The new version: a different graph with the original as parent. Its only run is its own remember run.
        var newVersion = await RememberAsync(memory, scope, NewVersion, "new-version", parent: original);

        // The close match: its remember run, 4 more successes and 5 failures make 5 out of 10.
        var closeMatch = await RememberAsync(memory, scope, CloseMatch, "close-match");
        await RunAsync(memory, scope, closeMatch, Success, 4);
        await RunAsync(memory, scope, closeMatch, Failure, 5);

        var result = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = Query }, Ct);

        // With k = 5 and freshness 1, the totals are 0.5 * similarity + 0.3 * successRate + 0.2:
        //   original:    successRate (40 + 5 * 0.5) / (40 + 5) = 42.5 / 45 = 0.94444
        //                total 0.425 + 0.28333 + 0.2 = 0.90833
        //   new version: its prior is the original's rate, 42.5 / 45, so successRate (1 + 5 * 42.5 / 45) / (1 + 5) = 0.95370
        //                total 0.43 + 0.28611 + 0.2 = 0.91611
        //   close match: successRate (5 + 5 * 0.5) / (10 + 5) = 7.5 / 15 = 0.5
        //                total 0.46 + 0.15 + 0.2 = 0.81
        Assert.True(result.IsSuccess);
        Assert.Equal([newVersion, original, closeMatch], result.Value.Select(m => m.Reasonlet.Id));
        AssertScore(result.Value[0].Score, 0.86, (1 + (5 * 42.5 / 45)) / 6);
        AssertScore(result.Value[1].Score, 0.85, 42.5 / 45);
        AssertScore(result.Value[2].Score, 0.92, 7.5 / 15);
        Assert.Equal("1.1", result.Value[0].Reasonlet.Version);
        Assert.Equal(original, result.Value[0].Reasonlet.ParentId);
    }

    [Fact]
    public async Task RecallReturnsEmptyBelowTheCutOff()
    {
        // The stored question's vector (0.4, sqrt(0.84), 0) has cosine 0.4 with the query's (1, 0, 0), below the default
        // cut-off of 0.5.
        var embeddings = new FixedEmbeddings { [Query] = [1f, 0f, 0f], [FarAway] = Unit(0.4) };
        var (memory, scope) = await MemoryAsync(embeddings);
        var far = await RememberAsync(memory, scope, FarAway, "far");

        var result = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = Query }, Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);

        // The store did return it: a query that lowers the cut-off gets it back, at similarity 0.4.
        var lowered = await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = Query, MinSimilarity = 0.3 }, Ct);

        var match = Assert.Single(lowered.Value);
        Assert.Equal(far, match.Reasonlet.Id);
        Assert.Equal(0.4, match.Score.Similarity, Tolerance);
    }

    [Fact]
    public async Task RecallAfterTheParentIsErasedUsesTheNeutralPrior()
    {
        // The new version's question embeds to the query's own vector, so its similarity is 1. The parent's has cosine 0.6.
        var embeddings = new FixedEmbeddings { [Query] = [1f, 0f, 0f], [NewVersion] = [1f, 0f, 0f], [Original] = Unit(0.6) };
        var (memory, scope) = await MemoryAsync(embeddings);
        var parent = await RememberAsync(memory, scope, Original, "original", subject: "erased");
        await RunAsync(memory, scope, parent, Success, 39, subject: "erased");
        var child = await RememberAsync(memory, scope, NewVersion, "new-version", parent: parent, subject: "kept");
        var query = new ReasonletQuery { Scope = scope, Question = Query };

        // While the parent lives, the child's prior is the parent's 40 out of 40: (1 + 5 * 42.5 / 45) / 6.
        var before = await memory.RecallAsync(query, Ct);
        Assert.Equal(child, before.Value[0].Reasonlet.Id);
        Assert.Equal((1 + (5 * 42.5 / 45)) / 6, before.Value[0].Score.SuccessRate, 1e-12);

        var erased = await memory.EraseAsync(new ReasonletErasure(scope, "erased"), Ct);
        Assert.Equal(1, erased.Value.Reasonlets);

        // The parent link is cut, so the child is a root: its 1 out of 1 is smoothed with 0.5, (1 + 2.5) / 6.
        var after = await memory.RecallAsync(query, Ct);

        var match = Assert.Single(after.Value);
        Assert.Equal(child, match.Reasonlet.Id);
        Assert.Null(match.Reasonlet.ParentId);
        AssertScore(match.Score, 1.0, 3.5 / 6);
    }

    [Fact]
    public async Task RememberThroughTheFacadeStoresTheDefaults()
    {
        const string question = "  \n  Export the monthly report to PDF  \nIt goes to finance on the first.";
        var embeddings = new FixedEmbeddings { [question] = [0.6f, 0.8f, 0f] };
        var (memory, scope) = await MemoryAsync(embeddings);

        var receipt = await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Question = question,
                Graph = Entries.Graph("defaults"),
                Outcome = Success,
                Provenance = new ReasonletProvenance { Source = "test" },
            },
            Ct);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(ReasonletCaptureKind.NewReasonlet, receipt.Value.Kind);
        Assert.Equal("unspecified", await ColumnAsync("intent", receipt.Value.Id));
        Assert.Equal("Export the monthly report to PDF", await ColumnAsync("name", receipt.Value.Id));
        Assert.Equal("[0.6,0.8,0]", await ColumnAsync("embedding::text", receipt.Value.Id));
    }

    /// <summary>A unit vector in the xy-plane whose cosine with (1, 0, 0) is <paramref name="similarity"/>.</summary>
    private static float[] Unit(double similarity) => [(float)similarity, (float)Math.Sqrt(1 - (similarity * similarity)), 0f];

    /// <summary>Asserts a score's similarity and success rate, a freshness of about 1, and the total the defaults give.</summary>
    private static void AssertScore(ReasonletScore score, double similarity, double successRate)
    {
        Assert.Equal(similarity, score.Similarity, Tolerance);
        Assert.Equal(successRate, score.SuccessRate, 1e-12);
        Assert.InRange(score.Freshness, FreshFloor, 1.0);
        Assert.Equal((0.5 * similarity) + (0.3 * successRate) + 0.2, score.Total, Tolerance);
    }

    private async Task<(ReasonletMemory Memory, ReasonletScope Scope)> MemoryAsync(FixedEmbeddings embeddings)
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var options = new MnemosyneOptions();
        var memory = new ReasonletMemory(store, new ReasonletSelector(options.Ranking, TimeProvider.System), embeddings, null, options);
        return (memory, PgVectorFixture.NewScope());
    }

    private static async Task<ReasonletId> RememberAsync(
        ReasonletMemory memory, ReasonletScope scope, string question, string variant, ReasonletId? parent = null, string? subject = null)
    {
        var result = await memory.RememberAsync(
            new ReasonletCapture
            {
                Scope = scope,
                Question = question,
                Graph = Entries.Graph(variant),
                Outcome = Success,
                Provenance = new ReasonletProvenance { Source = "test", SubjectId = subject },
                ParentId = parent,
            },
            Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal(parent is null ? ReasonletCaptureKind.NewReasonlet : ReasonletCaptureKind.NewVersion, result.Value.Kind);
        return result.Value.Id;
    }

    private static async Task RunAsync(
        ReasonletMemory memory, ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, int times, string? subject = null)
    {
        for (var i = 0; i < times; i++)
        {
            var result = await memory.RecordExecutionAsync(scope, id, outcome, new ReasonletProvenance { Source = "test", SubjectId = subject }, Ct);
            Assert.True(result.IsSuccess);
        }
    }

    private Task<string> ColumnAsync(string column, ReasonletId id) =>
        Entries.ScalarAsync<string>(fixture.DataSource, $"SELECT {column} FROM mnemosyne.reasonlets WHERE id = $1", id.Value);

    /// <summary>An embedding generator that maps each known text to a fixed vector, and throws for any other.</summary>
    private sealed class FixedEmbeddings : Dictionary<string, float[]>, IEmbeddingGenerator<string, Embedding<float>>
    {
        public FixedEmbeddings()
            : base(StringComparer.Ordinal)
        {
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(this[v]))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
