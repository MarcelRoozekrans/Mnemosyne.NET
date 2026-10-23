using Microsoft.Extensions.AI;
using Mnemosyne.Samples.StoreAndRecall;
using Xunit;

namespace Mnemosyne.NET.Tests.Samples;

// The sample's ranking assertion runs in CI on these vectors, so the generator must be deterministic and its
// similarities must follow word overlap.
public sealed class HashingEmbeddingGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameTextGivesAnIdenticalVector()
    {
        using var generator = new HashingEmbeddingGenerator();

        var first = await Embed(generator, "How do I refund an order?");
        var second = await Embed(generator, "How do I refund an order?");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("How do I refund an order?")]
    [InlineData("plan a team offsite")]
    [InlineData("order order order refund")]
    [InlineData("")]
    [InlineData("!!!")]
    public async Task EveryVectorHasUnitLength(string text)
    {
        using var generator = new HashingEmbeddingGenerator();

        var vector = await Embed(generator, text);

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, 1e-6);
    }

    [Fact]
    public async Task SharedWordsScoreHigherThanUnrelatedText()
    {
        using var generator = new HashingEmbeddingGenerator();

        var question = await Embed(generator, "How do I refund an order");
        var related = await Embed(generator, "refund an order quickly");
        var unrelated = await Embed(generator, "plan a team offsite");

        Assert.True(Cosine(question, related) > Cosine(question, unrelated));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    public async Task TextWithoutWordsPutsAllWeightInBucketZero(string text)
    {
        using var generator = new HashingEmbeddingGenerator();

        var vector = await Embed(generator, text);

        Assert.Equal(1f, vector[0]);
        Assert.All(vector.Skip(1), v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task TheHashIsTheSameInEveryProcess()
    {
        // FNV-1a 32 of "a" is 0xE40C292C, and 0xE40C292C mod 256 is 44. A per-process hash such as
        // string.GetHashCode would move the word to another bucket on the next run.
        using var generator = new HashingEmbeddingGenerator();

        var vector = await Embed(generator, "a");

        Assert.Equal(1f, vector[44]);
        Assert.Equal(1f, vector.Sum());
    }

    [Fact]
    public async Task TheOutputHasTheDeclaredDimensions()
    {
        using var generator = new HashingEmbeddingGenerator();

        var vector = await Embed(generator, "How do I refund an order?");

        Assert.Equal(256, HashingEmbeddingGenerator.Dimensions);
        Assert.Equal(HashingEmbeddingGenerator.Dimensions, vector.Length);
    }

    [Fact]
    public async Task EachValueGetsItsOwnEmbeddingInOrder()
    {
        using var generator = new HashingEmbeddingGenerator();

        var both = await generator.GenerateAsync(["refund an order", "plan a team offsite"], cancellationToken: Ct);

        Assert.Equal(2, both.Count);
        Assert.Equal(await Embed(generator, "refund an order"), both[0].Vector.ToArray());
        Assert.Equal(await Embed(generator, "plan a team offsite"), both[1].Vector.ToArray());
    }

    [Fact]
    public async Task CaseAndPunctuationDoNotChangeTheVector()
    {
        using var generator = new HashingEmbeddingGenerator();

        Assert.Equal(await Embed(generator, "refund an order"), await Embed(generator, "Refund AN order?!"));
    }

    private static async Task<float[]> Embed(HashingEmbeddingGenerator generator, string text)
    {
        var embeddings = await generator.GenerateAsync([text], cancellationToken: Ct);
        return Assert.Single(embeddings).Vector.ToArray();
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
}
