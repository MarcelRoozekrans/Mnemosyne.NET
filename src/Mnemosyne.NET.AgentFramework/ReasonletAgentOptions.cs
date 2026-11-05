using Microsoft.Agents.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.AgentFramework;

/// <summary>Settings for the Reasonlet context provider of an agent.</summary>
public sealed class ReasonletAgentOptions
{
    /// <summary>
    /// Gets how a run's scope is found. Defaults to <see cref="DefaultScope"/>. Returning null skips memory for the run;
    /// it must not be null itself.
    /// </summary>
    public Func<AIAgent, AgentSession?, ReasonletAgentScope?> ScopeResolver { get; init; } = DefaultScope;

    /// <summary>Gets whether the provider recalls Reasonlets and adds them to the run's instructions. Defaults to true.</summary>
    public bool Recall { get; init; } = true;

    /// <summary>Gets whether the provider captures the run as a transcript. Defaults to true.</summary>
    public bool Capture { get; init; } = true;

    /// <summary>
    /// Gets whether the provider adds the <c>find_procedure</c> tool to the run. Defaults to false, because it adds a tool
    /// the model sees.
    /// </summary>
    public bool ExposeFindProcedure { get; init; }

    /// <summary>
    /// Gets the lowest similarity, in [0, 1], a recalled Reasonlet needs to be suggested. Defaults to null, which keeps the
    /// selector's own threshold.
    /// </summary>
    public double? MinSimilarity { get; init; }

    /// <summary>
    /// Gets how long a recall may take before the run goes on without it. Must be positive and at most
    /// 4,294,967,294 milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/>. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan RecallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how a recalled match is written as a suggestion. Defaults to null, which uses the built-in format.</summary>
    public Func<ReasonletMatch, string>? Formatter { get; init; }

    /// <summary>Gets the source recorded on a captured run. Defaults to <c>mnemosyne.agent</c>; it must not be blank.</summary>
    public string Source { get; init; } = "mnemosyne.agent";

    /// <summary>The default resolver: the agent's name as the scope, with no subject.</summary>
    /// <param name="agent">The agent that is running.</param>
    /// <param name="session">The session of the run, which this resolver ignores.</param>
    /// <returns>The scope, or null when the agent has no name, so memory is skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is null.</exception>
    public static ReasonletAgentScope? DefaultScope(AIAgent agent, AgentSession? session)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return string.IsNullOrWhiteSpace(agent.Name) ? null : new ReasonletAgentScope(new ReasonletScope(agent.Name));
    }

    /// <summary>Validates the options.</summary>
    /// <returns>Success, or a validation failure whose path is the offending property.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotNull(ScopeResolver, nameof(ScopeResolver)),
        () => new ReasonletSuggestionOptions { MinSimilarity = MinSimilarity, RecallTimeout = RecallTimeout }.Validate(),
        () => Check.NotBlank(Source, nameof(Source)));
}
