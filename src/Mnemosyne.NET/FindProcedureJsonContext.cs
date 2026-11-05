using System.Text.Json.Serialization;

namespace Mnemosyne.NET;

/// <summary>One procedure the <c>find_procedure</c> tool returns to the model.</summary>
/// <param name="Id">The Reasonlet's id.</param>
/// <param name="Name">The Reasonlet's name.</param>
/// <param name="Version">The Reasonlet's version.</param>
/// <param name="Similarity">How well the Reasonlet matched the question, in [0, 1].</param>
/// <param name="SuccessRate">How often the Reasonlet succeeded, in [0, 1].</param>
/// <param name="Runs">How many times the Reasonlet ran.</param>
/// <param name="Steps">The tools the Reasonlet called, in order.</param>
internal sealed record FindProcedureItem(
    string Id,
    string Name,
    string Version,
    double Similarity,
    double SuccessRate,
    int Runs,
    IReadOnlyList<FindProcedureStep> Steps);

/// <summary>One step of a procedure the <c>find_procedure</c> tool returns.</summary>
/// <param name="Tool">The step's tool, else its node name, else its node type.</param>
/// <param name="Arguments">The names of the arguments the step passed, empty when the Reasonlet does not list any.</param>
internal sealed record FindProcedureStep(string Tool, IReadOnlyList<string> Arguments);

/// <summary>Source-generated JSON metadata for the <c>find_procedure</c> result, so serializing it needs no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FindProcedureItem[]))]
internal sealed partial class FindProcedureJsonContext : JsonSerializerContext;
