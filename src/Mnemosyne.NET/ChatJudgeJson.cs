using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.NET;

/// <summary>What the chat judge reads in its user message: the run and the procedure it followed.</summary>
/// <remarks>
/// It leaves out the agent's own outcome, which would bias the judge towards the agent's view, and the Reasonlet's name,
/// which adds nothing the intent and the procedure don't carry. The members serialize in declaration order.
/// </remarks>
/// <param name="Question">The run's question.</param>
/// <param name="Intent">The Reasonlet's intent.</param>
/// <param name="Procedure">The procedure graph as its canonical JSON, nested as a value rather than an escaped string.</param>
/// <param name="Answer">The run's answer.</param>
internal sealed record ChatJudgeState(string Question, string Intent, JsonElement Procedure, string Answer);

/// <summary>The chat judge's reply, once it has passed strict parsing.</summary>
/// <param name="Worked">Whether the answer resolves the question by following the procedure.</param>
/// <param name="Confidence">How sure the model is of <paramref name="Worked"/>, in [0, 1].</param>
internal readonly record struct ChatJudgeReply(bool Worked, double Confidence);

/// <summary>Source-generated JSON metadata for <see cref="ChatJudgeState"/>, so serializing it needs no reflection.</summary>
/// <remarks>
/// The reply is not read through this context: the generated deserializer takes the last of two duplicate keys silently,
/// so <see cref="ChatReasonletEvaluator"/> reads the reply's properties itself.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatJudgeState))]
internal sealed partial class ChatJudgeJsonContext : JsonSerializerContext;
