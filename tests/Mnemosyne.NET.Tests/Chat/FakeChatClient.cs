using Microsoft.Extensions.AI;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>
/// A hand-written <see cref="IChatClient"/> that records what the code under test sends and replies with a canned
/// <see cref="ChatResponse"/>, a reply computed from the request, or throws.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _reply;
    private Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? _stream;
    private readonly List<IEnumerable<ChatMessage>> _received = [];
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly List<ChatOptions?> _options = [];

    private FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> reply) => _reply = reply;

    /// <summary>Gets the messages of every call, in order, copied when the call was made.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

    /// <summary>Gets the message sequence instance of every call, in order, as it was passed.</summary>
    public IReadOnlyList<IEnumerable<ChatMessage>> Received => _received;

    /// <summary>Gets the options of every call, in order.</summary>
    public IReadOnlyList<ChatOptions?> Options => _options;

    /// <summary>Gets the token of the last call.</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Replies with one assistant message holding <paramref name="text"/>.</summary>
    public static FakeChatClient Replying(string text) =>
        new((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));

    /// <summary>Replies with <paramref name="response"/>.</summary>
    public static FakeChatClient Replying(ChatResponse response) => new((_, _, _) => Task.FromResult(response));

    /// <summary>Replies with what <paramref name="reply"/> computes from the request's messages, options and token.</summary>
    public static FakeChatClient Replying(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> reply) =>
        new(reply);

    /// <summary>Streams what <paramref name="stream"/> computes from the request's messages, options and token.</summary>
    public static FakeChatClient Streaming(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream) =>
        new((_, _, _) => throw new NotSupportedException()) { _stream = stream };

    /// <summary>Throws <paramref name="exception"/> from every call.</summary>
    public static FakeChatClient Throwing(Exception exception) => new((_, _, _) => throw exception);

    /// <summary>Honours the token: a cancelled call throws, as a real client does.</summary>
    public static FakeChatClient HonouringCancellation() => new((_, _, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The token was not cancelled.");
    });

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        _received.Add(messages);
        _requests.Add(request);
        _options.Add(options);
        LastToken = cancellationToken;
        return _reply(request, options, cancellationToken);
    }

    // Only a fake made with Streaming streams; the evaluator never does.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new NotSupportedException();
        }

        var request = messages.ToList();
        _received.Add(messages);
        _requests.Add(request);
        _options.Add(options);
        LastToken = cancellationToken;
        return _stream(request, options, cancellationToken);
    }

    // The fake offers no services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
