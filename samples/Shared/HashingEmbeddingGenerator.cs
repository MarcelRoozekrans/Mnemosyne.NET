using System.Text;
using Microsoft.Extensions.AI;

namespace Mnemosyne.Samples;

/// <summary>
/// A stand-in embedding generator, so the sample runs offline with no API key. It is NOT a real model: it only
/// measures word overlap, so "refund an order" and "return a purchase" look unrelated to it.
/// </summary>
/// <remarks>
/// <para>
/// It lowercases the text, splits it into runs of letters and digits, hashes each word into one of
/// <see cref="Dimensions"/> buckets and L2-normalises the counts. Text without a single word puts all its weight in
/// bucket 0, so every vector has unit length and the store accepts it. The hash is FNV-1a over the word's UTF-8 bytes,
/// not <see cref="string.GetHashCode()"/>, which changes from process to process: the vectors, and so the ranking the
/// sample checks, are the same on every run and every machine.
/// </para>
/// <para>
/// In an application, register a real model through Microsoft.Extensions.AI instead, and set the store's
/// <c>Dimensions</c> to that model's output size. The sample's README shows Ollama and OpenAI.
/// </para>
/// </remarks>
public sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The length of every vector this generator returns.</summary>
    public const int Dimensions = 256;

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)))));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static float[] Embed(string text)
    {
        var counts = new double[Dimensions];
        var words = 0;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                counts[Bucket(text.AsSpan(start, i - start))]++;
                words++;
                start = -1;
            }
        }

        if (words == 0)
        {
            counts[0] = 1;
        }

        var norm = Math.Sqrt(counts.Sum(c => c * c));
        return [.. counts.Select(c => (float)(c / norm))];
    }

    private static int Bucket(ReadOnlySpan<char> word)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(word.ToString().ToLowerInvariant()))
        {
            hash = unchecked((hash ^ b) * prime);
        }

        return (int)(hash % Dimensions);
    }
}
