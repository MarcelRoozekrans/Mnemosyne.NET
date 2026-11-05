using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Mnemosyne.Samples;

/// <summary>One tool call the <see cref="ScriptedChatClient"/> asks the host to make.</summary>
/// <param name="Name">The name of the tool to call.</param>
/// <param name="Arguments">The arguments to pass to the tool.</param>
public sealed record ScriptedToolCall(string Name, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// A stand-in chat client, so the sample runs offline with no API key. It is NOT a model: it understands nothing of the
/// conversation and only replays a fixed script.
/// </summary>
/// <remarks>
/// <para>
/// When the last message of a call is not a tool result, it replies with one assistant message holding a call of every
/// scripted tool, with ids <c>call-1</c>, <c>call-2</c> and so on. When the last message is a tool result, it replies with
/// the scripted answer. With no scripted calls it answers at once. It records a copy of every request, so a sample can
/// check what the host sent, system text included.
/// </para>
/// <para>
/// In an application, register a real chat model through Microsoft.Extensions.AI instead, for example an Ollama or
/// OpenAI <see cref="IChatClient"/>.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly ScriptedToolCall[] _toolCalls;
    private readonly string _answer;
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly List<ChatOptions?> _options = [];
    private readonly Lock _gate = new();

    /// <summary>Scripts the tool calls to ask for, then the answer to give once their results are back.</summary>
    /// <param name="toolCalls">The tool calls to request first; empty to answer at once.</param>
    /// <param name="answer">The text of the final answer.</param>
    public ScriptedChatClient(IReadOnlyList<ScriptedToolCall> toolCalls, string answer)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentNullException.ThrowIfNull(answer);
        _toolCalls = [.. toolCalls];
        _answer = answer;
    }

    /// <summary>Gets the messages of every call, in order, copied when the call was made.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Gets a clone of the options of every call, in order, taken when the call was made.</summary>
    public IReadOnlyList<ChatOptions?> Options
    {
        get
        {
            lock (_gate)
            {
                return [.. _options];
            }
        }
    }

    /// <summary>
    /// Gets the system text of a call: its system messages and its <see cref="ChatOptions.Instructions"/>, joined by a
    /// newline, or an empty string when there are none.
    /// </summary>
    /// <param name="call">The zero-based index of the call.</param>
    public string SystemText(int call)
    {
        lock (_gate)
        {
            var parts = _requests[call]
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text)
                .Append(_options[call]?.Instructions)
                .Where(t => !string.IsNullOrEmpty(t));
            return string.Join("\n", parts);
        }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Reply(messages, options));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var update in Reply(messages, options).ToChatResponseUpdates())
        {
            await Task.Yield();
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private ChatResponse Reply(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var copy = messages.ToList();
        lock (_gate)
        {
            _requests.Add(copy);
            _options.Add(options?.Clone());
        }

        var lastIsToolResult = copy.Count > 0 && copy[^1].Contents.Any(c => c is FunctionResultContent);
        if (lastIsToolResult || _toolCalls.Length == 0)
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _answer));
        }

        var calls = _toolCalls
            .Select((c, i) => (AIContent)new FunctionCallContent($"call-{i + 1}", c.Name, new Dictionary<string, object?>(c.Arguments)))
            .ToList();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, calls));
    }
}
