using System.Text;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>A request to capture a Reasonlet from a finished run.</summary>
public sealed record ReasonletCapture
{
    /// <summary>Gets the scope to capture into.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets the question that was answered. Must not be blank.</summary>
    public required string Question { get; init; }

    /// <summary>Gets the graph of the run. Must not be null.</summary>
    public required ReasonletGraph Graph { get; init; }

    /// <summary>Gets the outcome of the run.</summary>
    public required ReasonletOutcome Outcome { get; init; }

    /// <summary>Gets where the capture came from. Must not be null.</summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>Gets the optional intent text. When present it must not be blank.</summary>
    public string? Intent { get; init; }

    /// <summary>Gets the optional name. When present it must not be blank.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the run's optional answer. With a registered evaluator, it lets the run this capture records be judged. It
    /// reaches the evaluator and nothing else: the facade strips it before the capture reaches the store, so it is never
    /// stored, and never placed in an error, a telemetry signal or <see cref="ToString"/>, which shows it as
    /// <c>[redacted]</c>. When present it must be valid UTF-16. An answer that is blank or longer than 32,000 code points
    /// is not judged: the run is stored with the agent's own verdict, since an agent's unusable output must not lose its run.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>Gets the optional parent Reasonlet. When present it must not be the default id.</summary>
    public ReasonletId? ParentId { get; init; }

    /// <summary>Validates the capture.</summary>
    /// <returns>Success, or the first validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.NotBlank(Question, nameof(Question)),
        () => Check.NotNull(Graph, nameof(Graph)),
        () => Check.Nested(Graph.Validate(), nameof(Graph)),
        () => Check.Nested(Outcome.Validate(), nameof(Outcome)),
        () => Check.NotNull(Provenance, nameof(Provenance)),
        () => Check.Nested(Provenance.Validate(), nameof(Provenance)),
        () => Intent is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Intent, nameof(Intent)),
        () => Name is null ? UnitResult<MnemosyneError>.Success() : Check.NotBlank(Name, nameof(Name)),
        () => ParentId is { } parent ? Check.NotDefault(parent, nameof(ParentId)) : UnitResult<MnemosyneError>.Success(),
        () => Check.ValidUtf16(Answer, nameof(Answer)));

    /// <summary>Prints the members as the compiler would, except that the answer shows only whether it is present.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scope = ").Append(Scope.ToString());
        builder.Append(", Question = ").Append(Question);
        builder.Append(", Graph = ").Append(Graph);
        builder.Append(", Outcome = ").Append(Outcome.ToString());
        builder.Append(", Provenance = ").Append(Provenance);
        builder.Append(", Intent = ").Append(Intent);
        builder.Append(", Name = ").Append(Name);
        RedactedAnswer.Print(builder, Answer);
        builder.Append(", ParentId = ").Append(ParentId.ToString());
        return true;
    }
}
