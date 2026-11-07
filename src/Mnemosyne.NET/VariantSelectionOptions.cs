using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>How <see cref="VariantSelector"/> chooses between the variants of one lineage.</summary>
public enum VariantExploration
{
    /// <summary>Always return the best-ranked variant of each lineage.</summary>
    None,

    /// <summary>Draw each variant's success rate from its Beta posterior and return the highest draw.</summary>
    Thompson,
}

/// <summary>Settings for <see cref="VariantSelector"/>.</summary>
public sealed class VariantSelectionOptions
{
    private const int MaxMinTrials = 1000;

    /// <summary>Gets how a variant is picked within a lineage. Defaults to <see cref="VariantExploration.None"/>.</summary>
    public VariantExploration Exploration { get; init; } = VariantExploration.None;

    /// <summary>
    /// Gets the seed that makes exploration repeatable, or null for a different draw each time. Defaults to null.
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>
    /// Gets how many trials a variant needs before it is exempt from forced exploration, in [0, 1000].
    /// Defaults to 0, which turns forced exploration off.
    /// </summary>
    public int MinTrials { get; init; }

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Enum.IsDefined(Exploration)
            ? UnitResult<MnemosyneError>.Success()
            : Check.Fail(nameof(Exploration), "must be a defined value"),
        () => Check.Range(MinTrials, 0, MaxMinTrials, nameof(MinTrials)));
}
