using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Settings for the chat middleware that <c>UseReasonlets</c> adds.</summary>
public sealed class ReasonletChatOptions
{
    /// <summary>
    /// Gets the scope a call uses when it sets none with <see cref="ReasonletChatOptionsExtensions.WithReasonletScope"/>.
    /// Defaults to null: a call with neither passes straight through.
    /// </summary>
    public ReasonletScope? DefaultScope { get; init; }

    /// <summary>Gets whether the middleware recalls Reasonlets and injects them into the request. Defaults to true.</summary>
    public bool Recall { get; init; } = true;

    /// <summary>Gets whether the middleware captures the exchange as a transcript. Defaults to true.</summary>
    public bool Capture { get; init; } = true;

    /// <summary>
    /// Gets whether the middleware adds the <c>find_procedure</c> tool to the request. Defaults to false, because it adds a
    /// tool the model sees.
    /// </summary>
    public bool ExposeFindProcedure { get; init; }

    /// <summary>
    /// Gets the lowest similarity, in [0, 1], a recalled Reasonlet needs to be injected. Defaults to null, which keeps the
    /// selector's own threshold.
    /// </summary>
    public double? MinSimilarity { get; init; }

    /// <summary>
    /// Gets how long a recall may take before the call goes on without it. Must be positive and at most
    /// 4,294,967,294 milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/>. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan RecallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how a recalled match is written into the request. Defaults to null, which uses the built-in format.</summary>
    public Func<ReasonletMatch, string>? Formatter { get; init; }

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => DefaultScope is { } scope ? Check.Nested(scope.Validate(), nameof(DefaultScope)) : UnitResult<MnemosyneError>.Success(),
        () => ReasonletSuggestionOptions.ValidateRecall(MinSimilarity, RecallTimeout));
}
