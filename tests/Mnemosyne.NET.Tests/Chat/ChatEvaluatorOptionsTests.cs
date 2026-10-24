using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class ChatEvaluatorOptionsTests
{
    private const string Emoji = "\U0001F600";

    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new ChatEvaluatorOptions();

        Assert.Equal("llm-judge", options.EvaluatorName);
        Assert.Null(options.Criteria);
        Assert.Null(options.ChatClientKey);
        Assert.True(options.Validate().IsSuccess);
    }

    // ---- EvaluatorName ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankEvaluatorNameFails(string? name)
    {
        var result = new ChatEvaluatorOptions { EvaluatorName = name! }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must not be blank", result.Error.Message);
    }

    [Fact]
    public void AnEvaluatorNameWithALoneSurrogateFails()
    {
        var result = new ChatEvaluatorOptions { EvaluatorName = "judge\uD800" }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must be valid UTF-16", result.Error.Message);
    }

    [Fact]
    public void AnEvaluatorNameOfAHundredCodePointsPasses()
    {
        Assert.True(new ChatEvaluatorOptions { EvaluatorName = new string('x', 100) }.Validate().IsSuccess);

        // Surrogate pairs count once, the unit the evaluation's own limit counts.
        Assert.True(new ChatEvaluatorOptions { EvaluatorName = string.Concat(Enumerable.Repeat(Emoji, 100)) }.Validate().IsSuccess);
    }

    [Fact]
    public void AnEvaluatorNameOverAHundredCodePointsFails()
    {
        var result = new ChatEvaluatorOptions { EvaluatorName = string.Concat(Enumerable.Repeat(Emoji, 101)) }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("EvaluatorName: must not be longer than 100 code points", result.Error.Message);
    }

    [Fact]
    public void AValidNameIsAValidEvaluation()
    {
        // The options accept exactly the names a verdict accepts, so a valid option never yields an invalid verdict.
        foreach (var name in new[] { "llm-judge", new string('x', 100), string.Concat(Enumerable.Repeat(Emoji, 100)) })
        {
            Assert.True(new ChatEvaluatorOptions { EvaluatorName = name }.Validate().IsSuccess);
            Assert.True(new ReasonletEvaluation(true, 1, name).Validate().IsSuccess);
        }
    }

    // ---- Criteria ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void BlankCriteriaFail(string criteria)
    {
        var result = new ChatEvaluatorOptions { Criteria = criteria }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Criteria: must not be blank", result.Error.Message);
    }

    [Fact]
    public void CriteriaWithALoneSurrogateFail()
    {
        var result = new ChatEvaluatorOptions { Criteria = "cite a source \uDC00" }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Criteria: must be valid UTF-16", result.Error.Message);
    }

    [Fact]
    public void CriteriaOfFourThousandCodePointsPass()
    {
        Assert.True(new ChatEvaluatorOptions { Criteria = new string('x', 4_000) }.Validate().IsSuccess);

        // A surrogate pair counts as one code point, so 4,000 emoji are 8,000 chars and still pass.
        Assert.True(new ChatEvaluatorOptions { Criteria = string.Concat(Enumerable.Repeat(Emoji, 4_000)) }.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CriteriaOverFourThousandCodePointsFail(bool emoji)
    {
        var criteria = emoji ? string.Concat(Enumerable.Repeat(Emoji, 4_001)) : new string('x', 4_001);

        var result = new ChatEvaluatorOptions { Criteria = criteria }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("Criteria: must not be longer than 4000 code points", result.Error.Message);
    }

    [Fact]
    public void SetCriteriaAreValid() =>
        Assert.True(new ChatEvaluatorOptions { Criteria = "The answer must cite a source." }.Validate().IsSuccess);

    // ---- ChatClientKey ----

    [Fact]
    public void AnyChatClientKeyIsValid() =>
        Assert.True(new ChatEvaluatorOptions { ChatClientKey = "judge" }.Validate().IsSuccess);
}
