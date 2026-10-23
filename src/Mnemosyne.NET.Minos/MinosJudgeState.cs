using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.NET.Minos;

/// <summary>What the Minos judge reads: the run and the procedure it followed.</summary>
/// <remarks>
/// It leaves out the agent's own outcome, which would bias the judge towards the agent's view, and the Reasonlet's name,
/// which adds nothing the intent and the procedure don't carry. The members serialize in declaration order.
/// </remarks>
/// <param name="Question">The run's question.</param>
/// <param name="Intent">The Reasonlet's intent.</param>
/// <param name="Procedure">The procedure graph as its canonical JSON, nested as a value rather than an escaped string.</param>
/// <param name="Answer">The run's answer.</param>
internal sealed record MinosJudgeState(string Question, string Intent, JsonElement Procedure, string Answer);

/// <summary>Source-generated JSON metadata for <see cref="MinosJudgeState"/>, so serializing it needs no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MinosJudgeState))]
internal sealed partial class MinosJudgeJsonContext : JsonSerializerContext;
