using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>Builders for store entries and raw SQL helpers shared by the Docker-tier tests.</summary>
internal static class Entries
{
    /// <summary>
    /// A graph whose JSON jsonb would change: unsorted keys, a trailing-zero number, an exponent and non-ASCII text.
    /// <paramref name="variant"/> changes the answer node's name, so different variants hash differently.
    /// </summary>
    public static ReasonletGraph Graph(string variant = "a") => new()
    {
        Nodes =
        [
            new ReasonletNode
            {
                Id = "search",
                Type = "tool",
                Tool = "search",
                Name = "Zoek <é>",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["tool.args"] = JsonDocument.Parse("""{"z":1.50,"a":[1e2,true,null]}""").RootElement.Clone(),
                },
            },
            new ReasonletNode { Id = "answer", Type = "answer", Name = $"answer-{variant}" },
        ],
        Edges = [new ReasonletEdge { From = "search", To = "answer", Condition = "found" }],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    };

    public static ReasonletProvenance Provenance(IReadOnlyDictionary<string, string>? tags = null) => new()
    {
        Source = "test-source",
        RunId = "run-1",
        SubjectId = "subject-1",
        Tags = tags ?? new Dictionary<string, string> { ["z"] = "last", ["a"] = "first", ["é"] = "" },
    };

    public static ReasonletStoreEntry Entry(
        ReasonletScope scope,
        ReasonletGraph? graph = null,
        ReasonletId? parentId = null,
        ReadOnlyMemory<float>? embedding = null,
        ReasonletProvenance? provenance = null,
        ReasonletOutcome? outcome = null) => new(
        new ReasonletCapture
        {
            Scope = scope,
            Question = "How do I find it?",
            Graph = graph ?? Graph(),
            Outcome = outcome ?? new ReasonletOutcome(true, TimeSpan.FromMilliseconds(1234.5), 0.75),
            Provenance = provenance ?? Provenance(),
            ParentId = parentId,
        },
        "find-it",
        "Find it",
        embedding ?? new float[] { 1f, 0.5f, -0.25f });

    public static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, params object[] parameters)
    {
        await using var command = Command(dataSource, sql, parameters);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] parameters)
    {
        await using var command = Command(dataSource, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlDataSource dataSource, string sql, object[] parameters)
    {
        var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return command;
    }
}
