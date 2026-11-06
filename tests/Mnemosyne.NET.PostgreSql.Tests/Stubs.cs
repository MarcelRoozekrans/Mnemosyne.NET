using Microsoft.Extensions.AI;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// A model that calls the tools of <see cref="Script"/> in turn, one per request, then answers with plain text. It records
/// the messages and the instructions of every request.
/// </summary>
internal sealed class ScriptedModel : IChatClient
{
    private int _step;

    public string[] Script { get; set; } = [];

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<string?> Instructions { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        Instructions.Add(options?.Instructions);
        if (_step < Script.Length)
        {
            var name = Script[_step++];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{Requests.Count}", name)])));
        }

        _step = 0;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An embedding generator that gives every text the same vector, so a repeated question has similarity 1.</summary>
internal sealed class ConstantEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1f, 0.5f, -0.25f }))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
