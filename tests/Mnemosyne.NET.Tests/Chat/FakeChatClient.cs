using Microsoft.Extensions.AI;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>
/// A hand-written <see cref="IChatClient"/> that records what the code under test sends and replies with a canned
/// <see cref="ChatResponse"/>, or throws.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<CancellationToken, ChatResponse> _reply;
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly List<ChatOptions?> _options = [];

    private FakeChatClient(Func<CancellationToken, ChatResponse> reply) => _reply = reply;

    /// <summary>Gets the messages of every call, in order.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

    /// <summary>Gets the options of every call, in order.</summary>
    public IReadOnlyList<ChatOptions?> Options => _options;

    /// <summary>Gets the token of the last call.</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Replies with one assistant message holding <paramref name="text"/>.</summary>
    public static FakeChatClient Replying(string text) =>
        new(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    /// <summary>Replies with <paramref name="response"/>.</summary>
    public static FakeChatClient Replying(ChatResponse response) => new(_ => response);

    /// <summary>Throws <paramref name="exception"/> from every call.</summary>
    public static FakeChatClient Throwing(Exception exception) => new(_ => throw exception);

    /// <summary>Honours the token: a cancelled call throws, as a real client does.</summary>
    public static FakeChatClient HonouringCancellation() => new(ct =>
    {
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The token was not cancelled.");
    });

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _requests.Add(messages.ToList());
        _options.Add(options);
        LastToken = cancellationToken;
        return Task.FromResult(_reply(cancellationToken));
    }

    // The evaluator never streams.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // The fake offers no services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
