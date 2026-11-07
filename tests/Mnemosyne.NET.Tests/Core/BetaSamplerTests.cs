using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Core;

public sealed class BetaSamplerTests
{
    private const int Draws = 20_000;

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 5.0)]
    [InlineData(5.0, 2.0)]
    [InlineData(30.0, 10.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.3, 2.0)]
    [InlineData(2.0, 0.3)]
    public void TheMeanOfManyDrawsMatchesTheBetaMean(double alpha, double beta)
    {
        var sampler = new BetaSampler(new Random(12345));

        var sum = 0.0;
        for (var i = 0; i < Draws; i++)
        {
            sum += sampler.Sample(alpha, beta);
        }

        Assert.InRange(sum / Draws, (alpha / (alpha + beta)) - 0.02, (alpha / (alpha + beta)) + 0.02);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.2, 0.2)]
    [InlineData(0.5, 40.0)]
    [InlineData(40.0, 0.5)]
    public void EveryDrawIsInTheUnitInterval(double alpha, double beta)
    {
        var sampler = new BetaSampler(new Random(7));

        for (var i = 0; i < Draws; i++)
        {
            Assert.InRange(sampler.Sample(alpha, beta), 0.0, 1.0);
        }
    }

    [Fact]
    public void TheSameSeedRepeatsTheSequence()
    {
        var first = new BetaSampler(new Random(99));
        var second = new BetaSampler(new Random(99));

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(first.Sample(2.5, 1.5), second.Sample(2.5, 1.5));
        }
    }

    [Fact]
    public void ParametersSoSmallThatBothDrawsVanishFallBackToTheMean()
    {
        var sampler = new BetaSampler(new Random(1));

        Assert.Equal(0.5, sampler.Sample(1e-300, 1e-300));
        Assert.Equal(0.25, sampler.Sample(1e-300, 3e-300));
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void ANonPositiveOrNonFiniteParameterThrows(double alpha, double beta)
    {
        var sampler = new BetaSampler(new Random(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Sample(alpha, beta));
    }

    [Fact]
    public void SeededRandomIsEqualForEqualInputs()
    {
        var lineage = new ReasonletId(Guid.Parse("11111111-2222-3333-4444-555555555555"));

        var first = BetaSampler.SeededRandom(42, "how do I rebase", lineage);
        var second = BetaSampler.SeededRandom(42, "how do I rebase", lineage);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first.Next(), second.Next());
        }
    }

    [Fact]
    public void SeededRandomDiffersWhenTheSeedTheQuestionOrTheLineageDiffers()
    {
        var lineage = new ReasonletId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var other = new ReasonletId(Guid.Parse("11111111-2222-3333-4444-555555555556"));
        var baseline = BetaSampler.SeededRandom(42, "how do I rebase", lineage).Next();

        Assert.NotEqual(baseline, BetaSampler.SeededRandom(43, "how do I rebase", lineage).Next());
        Assert.NotEqual(baseline, BetaSampler.SeededRandom(42, "how do I merge", lineage).Next());
        Assert.NotEqual(baseline, BetaSampler.SeededRandom(42, "how do I rebase", other).Next());
    }
}
