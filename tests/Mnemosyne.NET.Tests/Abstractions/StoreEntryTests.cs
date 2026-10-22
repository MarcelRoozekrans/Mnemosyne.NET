using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class StoreEntryTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    [Fact]
    public void Valid_Passes() => Assert.True(TestData.StoreEntry().Validate().IsSuccess);

    [Fact]
    public void CaptureValuesEqualToResolved_Pass() =>
        Assert.True((TestData.StoreEntry() with { Capture = TestData.Capture() with { Intent = "intent", Name = "name" } }).Validate().IsSuccess);

    [Fact]
    public void NullCapture_ReportsCapture() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = null! }).Validate(), "Capture");

    [Fact]
    public void InvalidCapture_ReportsNestedPath() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Question = " " } }).Validate(), "Capture.Question");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankIntent_Fails(string? intent) =>
        AssertInvalid((TestData.StoreEntry() with { Intent = intent! }).Validate(), "Intent");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankName_Fails(string? name) =>
        AssertInvalid((TestData.StoreEntry() with { Name = name! }).Validate(), "Name");

    [Fact]
    public void IntentDifferentFromCapture_Fails() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Intent = "other" } }).Validate(), "Intent");

    [Fact]
    public void NameDifferentFromCapture_Fails() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Name = "other" } }).Validate(), "Name");

    [Fact]
    public void CaptureIntentComparedOrdinally_Fails() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Intent = "INTENT" } }).Validate(), "Intent");

    [Fact]
    public void EmptyEmbedding_Fails() =>
        AssertInvalid((TestData.StoreEntry() with { Embedding = ReadOnlyMemory<float>.Empty }).Validate(), "Embedding");

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteEmbeddingValue_ReportsIndex(float value) =>
        AssertInvalid((TestData.StoreEntry() with { Embedding = new[] { 0.1f, 0.2f, value } }).Validate(), "Embedding[2]");

    [Fact]
    public void ReportsFirstErrorInOrder() =>
        AssertInvalid(
            (TestData.StoreEntry() with { Intent = "", Name = "", Embedding = ReadOnlyMemory<float>.Empty }).Validate(),
            "Intent");

    [Fact]
    public void NameIsCheckedBeforeEmbedding() =>
        AssertInvalid(
            (TestData.StoreEntry() with { Name = "", Embedding = ReadOnlyMemory<float>.Empty }).Validate(),
            "Name");
}
