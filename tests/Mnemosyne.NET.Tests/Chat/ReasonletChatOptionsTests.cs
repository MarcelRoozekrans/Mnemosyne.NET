using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

public sealed class ReasonletChatOptionsTests
{
    [Fact]
    public void DefaultsAreRecallCaptureNoToolFiveSeconds()
    {
        var options = new ReasonletChatOptions();

        Assert.Null(options.DefaultScope);
        Assert.True(options.Recall);
        Assert.True(options.Capture);
        Assert.False(options.ExposeFindProcedure);
        Assert.Null(options.MinSimilarity);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RecallTimeout);
        Assert.Null(options.Formatter);
        Assert.True(options.Validate().IsSuccess);
    }

    [Fact]
    public void AValidDefaultScopeAndSimilarityPass()
    {
        var options = new ReasonletChatOptions { DefaultScope = new ReasonletScope("support"), MinSimilarity = 0.5 };

        Assert.True(options.Validate().IsSuccess);
    }

    [Fact]
    public void InvalidDefaultScopeFails()
    {
        var result = new ReasonletChatOptions { DefaultScope = new ReasonletScope(" ") }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("DefaultScope.Value:", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void MinSimilarityOutsideUnitIntervalFails(double value)
    {
        var result = new ReasonletChatOptions { MinSimilarity = value }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("MinSimilarity:", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveRecallTimeoutFails()
    {
        var result = new ReasonletChatOptions { RecallTimeout = TimeSpan.Zero }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("RecallTimeout:", result.Error.Message, StringComparison.Ordinal);
        Assert.True(new ReasonletChatOptions { RecallTimeout = Timeout.InfiniteTimeSpan }.Validate().IsSuccess);
    }

    [Fact]
    public void WithReasonletScopeRoundTrips()
    {
        var options = new ChatOptions().WithReasonletScope(new ReasonletScope("support"), "u-1");

        Assert.Equal(new ReasonletCallScope(new ReasonletScope("support"), "u-1"), options.GetReasonletScope());
    }

    [Fact]
    public void WithReasonletScopeMutatesAndReturnsTheGivenOptions()
    {
        var given = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { ["other"] = 1 } };

        var returned = given.WithReasonletScope(new ReasonletScope("support"));

        Assert.Same(given, returned);
        Assert.Equal(1, given.AdditionalProperties["other"]);
        Assert.Null(given.GetReasonletScope()!.SubjectId);
    }

    [Fact]
    public void WithReasonletScopeOnNullCreatesOptions()
    {
        ChatOptions? none = null;

        var options = none.WithReasonletScope(new ReasonletScope("support"));

        Assert.Equal("support", options.GetReasonletScope()!.Scope.Value);
    }

    [Fact]
    public void WithReasonletScopeRejectsInvalidScope()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ChatOptions().WithReasonletScope(new ReasonletScope(" ")));

        Assert.Equal("scope", ex.ParamName);
    }

    [Fact]
    public void GetReasonletScopeIsNullWithoutKeyOrForAForeignValue()
    {
        ChatOptions? none = null;

        Assert.Null(none.GetReasonletScope());
        Assert.Null(new ChatOptions().GetReasonletScope());
        Assert.Null(new ChatOptions { AdditionalProperties = [] }.GetReasonletScope());
        Assert.Null(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ReasonletChatOptionsExtensions.ScopeKey] = "support" },
        }.GetReasonletScope());
    }
}
