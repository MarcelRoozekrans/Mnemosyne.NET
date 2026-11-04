using System.Globalization;
using System.Text;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Renders a recalled Reasonlet as the text of a suggestion system message.</summary>
/// <remarks>
/// The text names the Reasonlet, how well it matched and how often it succeeded, then lists its steps in topological order,
/// the smallest node id first among the nodes that are ready. A step's label is the node's tool, else its name, else its type.
/// Its arguments are the names in the <c>mnemosyne.arguments</c> property when that is a JSON array of strings.
/// The success rate is a whole percent with no space before the sign, because the invariant percent pattern adds one. Numbers use <see cref="CultureInfo.InvariantCulture"/>.
/// </remarks>
public static class ReasonletPromptFormatter
{
    private const string ArgumentsProperty = "mnemosyne.arguments";

    /// <summary>Renders the match as the suggestion text.</summary>
    /// <param name="match">The recalled Reasonlet. Its graph must be valid.</param>
    /// <returns>The text: two header lines, then one line per step.</returns>
    public static string Format(ReasonletMatch match)
    {
        var text = new StringBuilder("A procedure that worked for a similar question before. It is a suggestion: follow it only where it fits.");
        text.Append(CultureInfo.InvariantCulture, $"\nName: {match.Reasonlet.Name} · match {match.Score.Similarity:F2} · success {match.Score.SuccessRate * 100:F0}% over {match.Evidence.Runs} runs");
        var steps = Steps(match.Reasonlet.Graph);
        for (var i = 0; i < steps.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{i + 1}. {steps[i].Label}");
            if (steps[i].Arguments is { } arguments)
            {
                text.Append(" (").AppendJoin(", ", arguments).Append(')');
            }
        }

        return text.ToString();
    }

    /// <summary>Lists the graph's nodes as steps, in topological order with the smallest ready id first.</summary>
    /// <param name="graph">A graph that passed <see cref="ReasonletGraph.Validate"/>.</param>
    /// <returns>One step per node.</returns>
    /// <exception cref="InvalidOperationException">The graph has a cycle.</exception>
    internal static IReadOnlyList<Step> Steps(ReasonletGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var incoming = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            incoming[edge.To]++;
            outgoing[edge.From].Add(edge.To);
        }

        var ready = new SortedSet<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var steps = new List<Step>(nodes.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            steps.Add(ToStep(nodes[id]));
            foreach (var next in outgoing[id])
            {
                if (--incoming[next] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        return steps.Count == nodes.Count ? steps : throw new InvalidOperationException("The Reasonlet graph has a cycle.");
    }

    private static Step ToStep(ReasonletNode node) => new(node.Tool ?? node.Name ?? node.Type, ArgumentsOf(node));

    private static IReadOnlyList<string>? ArgumentsOf(ReasonletNode node)
    {
        if (node.Properties is null
            || !node.Properties.TryGetValue(ArgumentsProperty, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            names.Add(item.GetString()!);
        }

        return names;
    }

    /// <summary>One step of a rendered procedure.</summary>
    /// <param name="Label">The tool, else the node name, else the node type.</param>
    /// <param name="Arguments">The argument names, or null when the node does not list any.</param>
    internal readonly record struct Step(string Label, IReadOnlyList<string>? Arguments);
}
