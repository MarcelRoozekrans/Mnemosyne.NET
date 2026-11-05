using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>The <c>find_procedure</c> tool: recalls procedures for a question and returns them as JSON.</summary>
/// <remarks>
/// It is written by hand, with a literal schema and source-generated serialization, so it needs no reflection and is safe under AOT.
/// A match carries no intent: the intent is text extracted from one user's question, and the users of a shared scope would see each other's.
/// A failed lookup tells the model only that the tool is unavailable, never why, so no store detail reaches the prompt.
/// </remarks>
public sealed class FindProcedureFunction : AIFunction
{
    private const int DefaultTop = 3;
    private const int MaxTop = 5;

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"question":{"type":"string","description":"The question or task to find a procedure for."},"top":{"type":"integer","minimum":1,"maximum":5,"description":"How many procedures to return. Defaults to 3."}},"required":["question"],"additionalProperties":false}""").RootElement;

    private static readonly JsonElement InvalidArguments = JsonDocument.Parse("""{"error":"invalid_arguments"}""").RootElement;
    private static readonly JsonElement Unavailable = JsonDocument.Parse("""{"error":"unavailable"}""").RootElement;

    private readonly IReasonletMemory _memory;
    private readonly ReasonletScope _scope;
    private readonly ConcurrentQueue<IReadOnlyList<ReasonletMatch>> _lookups = new();

    /// <summary>Initializes the tool.</summary>
    /// <param name="memory">The memory to recall from.</param>
    /// <param name="scope">The scope every lookup runs in.</param>
    internal FindProcedureFunction(IReasonletMemory memory, ReasonletScope scope)
    {
        _memory = memory;
        _scope = scope;
    }

    /// <summary>Gets the matches of every successful lookup, in call order. A lookup that found nothing is an empty list.</summary>
    /// <remarks>Each read returns a new snapshot, so the result does not change as later lookups run and is safe to read from any thread.</remarks>
    public IReadOnlyList<IReadOnlyList<ReasonletMatch>> Lookups => [.. _lookups];

    /// <inheritdoc />
    public override string Name => ReasonletTools.FindProcedureName;

    /// <inheritdoc />
    public override string Description =>
        "Looks up procedures that worked before for a similar question. Each has the tools it called, in order, with their argument names.";

    /// <inheritdoc />
    public override JsonElement JsonSchema => Schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!TryReadQuestion(arguments, out var question) || !TryReadTop(arguments, out var top))
        {
            return InvalidArguments;
        }

        try
        {
            var recalled = await _memory
                .RecallAsync(new ReasonletQuery { Scope = _scope, Question = question, Top = top }, cancellationToken)
                .ConfigureAwait(false);
            if (recalled.IsFailure)
            {
                return Unavailable;
            }

            var matches = recalled.Value;
            var result = JsonSerializer.SerializeToElement(
                matches.Select(ToItem).ToArray(),
                FindProcedureJsonContext.Default.FindProcedureItemArray);
            _lookups.Enqueue(matches);
            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the caller's own cancellation propagates; a memory that cancels itself, as on a timeout, is unavailable.
            return Unavailable;
        }
    }

    private static FindProcedureItem ToItem(ReasonletMatch match) => new(
        match.Reasonlet.Id.ToString(),
        match.Reasonlet.Name,
        match.Reasonlet.Version,
        match.Score.Similarity,
        match.Score.SuccessRate,
        match.Evidence.Runs,
        [.. ReasonletPromptFormatter.Steps(match.Reasonlet.Graph).Select(s => new FindProcedureStep(s.Label, s.Arguments ?? []))]);

    private static bool TryReadQuestion(AIFunctionArguments arguments, out string question)
    {
        question = string.Empty;
        arguments.TryGetValue("question", out var value);
        var text = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        question = text;
        return true;
    }

    private static bool TryReadTop(AIFunctionArguments arguments, out int top)
    {
        top = DefaultTop;
        if (!arguments.TryGetValue("top", out var value) || value is null or JsonElement { ValueKind: JsonValueKind.Null })
        {
            // A model that sends "top": null means no preference, as one that leaves it out does.
            return true;
        }

        var number = value switch
        {
            int i => i,

            // The lower bound is not redundant: a cast to int keeps only the low 32 bits, so a large negative long could
            // land in range.
            long l when l is >= 0 and <= MaxTop => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var i) => i,
            _ => (int?)null,
        };
        if (number is not (>= 1 and <= MaxTop))
        {
            return false;
        }

        top = number.Value;
        return true;
    }
}
