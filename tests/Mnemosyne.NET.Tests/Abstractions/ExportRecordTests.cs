using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class ExportRecordTests
{
    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    private static ReasonletRecord Record() => new()
    {
        Definition = TestData.Definition(),
        Metrics = new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch),
    };

    private static ReasonletExecutionRecord Execution() => new()
    {
        Id = ExecutionId.New(),
        ReasonletId = ReasonletId.New(),
        Scope = new ReasonletScope("scope"),
        ExecutedAt = DateTimeOffset.UnixEpoch,
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
        Evaluation = new ReasonletEvaluation(true, 0.5, "judge"),
        Feedback = new ReasonletFeedback(0.1, 0.2, 0.3, true),
        Success = true,
        VerdictSource = ReasonletVerdictSource.Agent,
        Provenance = TestData.Provenance(),
    };

    [Fact]
    public void ARecordWithAValidDefinitionAndMetricsValidates() =>
        Assert.True(Record().Validate().IsSuccess);

    [Fact]
    public void ARecordWithAnInvalidMetricsNamesTheMetricsPath() =>
        AssertInvalid(
            (Record() with { Metrics = new ReasonletMetrics(1, 2, null, DateTimeOffset.UnixEpoch) }).Validate(),
            "Metrics.Successes");

    [Fact]
    public void ARecordWithAnInvalidDefinitionNamesTheDefinitionPath() =>
        AssertInvalid(
            (Record() with { Definition = TestData.Definition() with { Name = " " } }).Validate(),
            "Definition.Name");

    [Fact]
    public void ARecordWithANullDefinitionFails() =>
        AssertInvalid((Record() with { Definition = null! }).Validate(), "Definition");

    [Fact]
    public void ARecordWithCopyKeepsTheOtherPart()
    {
        var record = Record();
        var copy = record with { Metrics = new ReasonletMetrics(2, 1, null, DateTimeOffset.UnixEpoch) };
        Assert.Same(record.Definition, copy.Definition);
        Assert.Equal(2, copy.Metrics.Runs);
        Assert.NotEqual(record, copy);
    }

    [Fact]
    public void AnExecutionRecordWithAllPartsValidates()
    {
        Assert.True(Execution().Validate().IsSuccess);
        Assert.True((Execution() with { Evaluation = null, Feedback = null }).Validate().IsSuccess);
    }

    [Fact]
    public void AnExecutionRecordWithADefaultIdScopeOrReasonletIdFails()
    {
        AssertInvalid((Execution() with { Id = default }).Validate(), "Id");
        AssertInvalid((Execution() with { ReasonletId = default }).Validate(), "ReasonletId");
        AssertInvalid((Execution() with { Scope = default }).Validate(), "Scope.Value");
    }

    [Fact]
    public void AnExecutionRecordWithAnInvalidOutcomeEvaluationOrFeedbackFails()
    {
        AssertInvalid(
            (Execution() with { Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(-1), null) }).Validate(),
            "Outcome.Duration");
        AssertInvalid((Execution() with { Evaluation = new ReasonletEvaluation(true, 2, "judge") }).Validate(), "Evaluation.Score");
        AssertInvalid((Execution() with { Feedback = new ReasonletFeedback(2, 0, 0) }).Validate(), "Feedback.Utility");
    }

    [Fact]
    public void AnExecutionRecordWithAnUndefinedVerdictSourceFails() =>
        AssertInvalid((Execution() with { VerdictSource = (ReasonletVerdictSource)99 }).Validate(), "VerdictSource");

    [Fact]
    public void AnExecutionRecordWithAnInvalidProvenanceOrNullProvenanceFails()
    {
        AssertInvalid((Execution() with { Provenance = null! }).Validate(), "Provenance");
        AssertInvalid((Execution() with { Provenance = new ReasonletProvenance { Source = " " } }).Validate(), "Provenance.Source");
    }

    [Fact]
    public void AnExecutionRecordWithCopyChangesOnlyTheNamedPart()
    {
        var record = Execution();
        var copy = record with { Success = false };
        Assert.False(copy.Success);
        Assert.Equal(record.Id, copy.Id);
        Assert.NotEqual(record, copy);
    }
}
