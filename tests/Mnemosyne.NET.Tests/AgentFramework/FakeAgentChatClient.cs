using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Mnemosyne.NET.Tests.AgentFramework;

/// <summary>
/// A scripted <see cref="IChatClient"/> under a <c>ChatClientAgent</c>: each inner call replies with the next response of the
/// script, the last one repeating, and records a copy of the messages and options it was given.
/// </summary>
internal sealed class FakeAgentChatClient : IChatClient
{
    /// <summary>The name of the host tool the tool loop calls.</summary>
    public const string HostToolName = "orders_get";

    private readonly ChatResponse[] _script;
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly List<ChatOptions?> _options = [];
    private readonly Lock _gate = new();

    /// <summary>Replies with <paramref name="script"/>, one response per call, the last one repeating.</summary>
    public FakeAgentChatClient(params ChatResponse[] script)
    {
        if (script.Length == 0)
        {
            throw new ArgumentException("The script needs at least one response.", nameof(script));
        }

        _script = script;
    }

    /// <summary>Gets the messages of every inner call, in order, copied when the call was made.</summary>
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

    /// <summary>Gets a clone of the options of every inner call, in order, taken when the call was made.</summary>
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

    /// <summary>A one-tool loop: a call of <see cref="HostToolName"/>, then the final answer.</summary>
    public static FakeAgentChatClient ToolLoop() => new(
        new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", HostToolName, new Dictionary<string, object?> { ["id"] = 7 })])),
        new ChatResponse(new ChatMessage(ChatRole.Assistant, "Refunded.")));

    /// <summary>One plain answer.</summary>
    public static FakeAgentChatClient Answering(string text = "Done.") => new(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    /// <summary>The host tool the tool loop calls.</summary>
    public static AIFunction HostTool() => AIFunctionFactory.Create(() => "found", HostToolName);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Record(messages, options));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = Record(messages, options);
        foreach (var update in response.ToChatResponseUpdates())
        {
            await Task.Yield();
            yield return update;
        }
    }

    // The fake offers no services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ChatResponse Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        lock (_gate)
        {
            _requests.Add([.. messages]);
            _options.Add(options?.Clone());
            return _script[Math.Min(_requests.Count - 1, _script.Length - 1)];
        }
    }
}
