using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.AgentFramework;

/// <summary>
/// An Agent Framework context provider that recalls the best known procedure for each new question of an agent run and
/// adds it to the run's instructions, and can offer the <c>find_procedure</c> tool to the run.
/// </summary>
/// <remarks>
/// <para>
/// Build one provider per agent, with that agent's options, and add it to the agent's <c>AIContextProviders</c>. One
/// instance serves every session of the agent at once: what a run needs at its end is kept against its session, never in
/// the provider's fields.
/// </para>
/// <para>
/// Before each run, the provider asks <see cref="ReasonletAgentOptions.ScopeResolver"/> for the run's scope. A null scope
/// skips memory for the run, counted with skip reason <c>no_scope</c> when recall or capture is on. A resolver that throws
/// is counted as <c>exception</c> on <c>mnemosyne.chat.recalls</c> and treated as no scope.
/// </para>
/// <para>
/// With <see cref="ReasonletAgentOptions.Recall"/> on, the question is the text of the last user message among the run's
/// new messages. A run whose last user message has no text, such as one that only answers an approval request or only
/// holds an image, is not recalled for, counted with skip reason <c>continuation</c>. The top match is rendered and
/// returned as <see cref="AIContext.Instructions"/>, never as a message, so it reaches every model call of the run and is
/// never saved into the conversation's history. No match, an error, a timeout or an exception leaves the instructions out.
/// </para>
/// <para>
/// With <see cref="ReasonletAgentOptions.ExposeFindProcedure"/> on, each run gets its own <c>find_procedure</c> tool,
/// unless the agent or the run already has a tool of that name.
/// </para>
/// <para>
/// The provider resolves <see cref="IReasonletMemory"/> on the first run that needs it, not when it is built, so a memory
/// whose evaluator judges with the agent's own chat client forms no cycle, and a missing registration never breaks the
/// agent's construction. A memory that cannot be resolved is counted once per run as <c>exception</c> on
/// <c>mnemosyne.chat.recalls</c>, and the run goes on without recall and tool; the next run tries again. It resolves the
/// registered <see cref="TimeProvider"/>, or uses <see cref="TimeProvider.System"/>, when it is built.
/// </para>
/// <para>
/// Memory never breaks or stops a run. Only the caller's own cancellation propagates. Every signal carries
/// <c>mnemosyne.integration = agent</c>, and none carries the scope, the subject, the question, the suggested text, a tool
/// or argument name, or an exception message.
/// </para>
/// </remarks>
public sealed class ReasonletContextProvider : AIContextProvider
{
    private readonly ReasonletAgentOptions _options;
    private readonly ReasonletSuggestionOptions _suggestions;
    private readonly TimeProvider _time;
    private readonly Lazy<IReasonletMemory> _memory;

    // Keyed on the session, which both hooks of one run receive, so that concurrent runs on other sessions never see it.
    private readonly ConditionalWeakTable<AgentSession, AgentRunState> _runs = new();

    /// <summary>Creates the provider for one agent.</summary>
    /// <param name="services">The services to resolve <see cref="IReasonletMemory"/> and <see cref="TimeProvider"/> from.</param>
    /// <param name="options">The agent's settings, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ReasonletContextProvider(IServiceProvider services, ReasonletAgentOptions? options = null)
        : base(null, null, null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ReasonletAgentOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _options = options;
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _suggestions = new ReasonletSuggestionOptions
        {
            MinSimilarity = options.MinSimilarity,
            RecallTimeout = options.RecallTimeout,
            Formatter = options.Formatter,
            TimeProvider = _time,
        };

        // Resolved on the first run, not here: the memory can depend on the agent's own chat client, and a failure is not
        // cached, so the next run tries again.
        _memory = new Lazy<IReasonletMemory>(services.GetRequiredService<IReasonletMemory>, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>Gets no keys: the provider keeps nothing in the session's state bag.</summary>
    public override IReadOnlyList<string> StateKeys => [];

    /// <summary>Recalls for the run's new question and offers the <c>find_procedure</c> tool, as the options say.</summary>
    /// <param name="context">The run's agent, session and new messages.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <returns>The suggestion as instructions and the run's tool, either of which can be absent.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = ResolveScope(context, cancellationToken);
        if (scope is null)
        {
            // Nothing of an earlier run on this session, such as an abandoned stream, may reach this run's end.
            Forget(context.Session);
            return new AIContext();
        }

        var question = Question(context.AIContext.Messages);
        var wantsTool = _options.ExposeFindProcedure && !HasFindProcedure(context.AIContext.Tools);
        var memory = (_options.Recall && question is not null) || wantsTool ? ResolveMemory(cancellationToken) : null;
        ReasonletSuggestion? suggestion = null;
        if (_options.Recall)
        {
            if (question is null)
            {
                ReasonletSuggestions.CountSkipped(ReasonletSuggestions.ContinuationReason, ReasonletSuggestions.AgentIntegration);
            }
            else if (memory is not null)
            {
                suggestion = await ReasonletSuggestions.RecallAsync(
                    memory, scope.Scope, question, _suggestions, ReasonletSuggestions.AgentIntegration, cancellationToken).ConfigureAwait(false);
            }
        }

        var lookup = wantsTool && memory is not null ? ReasonletTools.CreateFindProcedure(memory, scope.Scope) : null;
        if (context.Session is { } session && _options.Capture)
        {
            // A new run replaces whatever an earlier run on the session left behind.
            _runs.AddOrUpdate(session, new AgentRunState(scope, suggestion?.Id, lookup, _time.GetTimestamp()));
        }

        return new AIContext { Instructions = suggestion?.Text, Tools = lookup is null ? null : [lookup] };
    }

    /// <summary>
    /// Asks the resolver for the run's scope. A null scope is counted as <c>no_scope</c> when recall or capture is on; a
    /// resolver that throws is counted as <c>exception</c> and gives no scope. The caller's own cancellation propagates.
    /// </summary>
    private ReasonletAgentScope? ResolveScope(InvokingContext context, CancellationToken ct)
    {
        ReasonletAgentScope? scope;
        try
        {
            scope = _options.ScopeResolver(context.Agent, context.Session);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            ReasonletSuggestions.CountFailed(ex, ReasonletSuggestions.AgentIntegration);
            return null;
        }

        // Counted whenever the run would have done something: with both switches off it was never going to.
        if (scope is null && (_options.Recall || _options.Capture))
        {
            ReasonletSuggestions.CountSkipped(ReasonletSuggestions.NoScopeReason, ReasonletSuggestions.AgentIntegration);
        }

        return scope;
    }

    /// <summary>
    /// Resolves the memory for this run. A resolution that throws is counted as <c>exception</c> and gives no memory; the
    /// caller's own cancellation propagates.
    /// </summary>
    private IReasonletMemory? ResolveMemory(CancellationToken ct)
    {
        try
        {
            return _memory.Value;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            ReasonletSuggestions.CountFailed(ex, ReasonletSuggestions.AgentIntegration);
            return null;
        }
    }

    private void Forget(AgentSession? session)
    {
        if (session is not null)
        {
            _runs.Remove(session);
        }
    }

    /// <summary>The text of the last user message, or null when there is none or it has no text.</summary>
    private static string? Question(IEnumerable<ChatMessage>? messages)
    {
        ChatMessage? last = null;
        foreach (var message in messages ?? [])
        {
            if (message.Role == ChatRole.User)
            {
                last = message;
            }
        }

        return last?.Text is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static bool HasFindProcedure(IEnumerable<AITool>? tools)
    {
        foreach (var tool in tools ?? [])
        {
            if (tool.Name == ReasonletTools.FindProcedureName)
            {
                return true;
            }
        }

        return false;
    }
}
