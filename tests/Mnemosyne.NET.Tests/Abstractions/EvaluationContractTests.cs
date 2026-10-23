using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

/// <summary>Validation of the phase 2.1 contract: the run, the evaluation request and verdict, the capture's answer and the feedback verdict.</summary>
public class EvaluationContractTests
{
    /// <summary>The most code points a run's question or answer may hold.</summary>
    private const int MaxText = 32_000;

    /// <summary>A lone high surrogate, which cannot be written as UTF-8.</summary>
    private const string LoneSurrogate = "a\uD800b";

    private static void AssertInvalid(UnitResult<MnemosyneError> r, string path)
    {
        Assert.True(r.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code);
        Assert.StartsWith(path + ": ", r.Error.Message);
    }

    private static ReasonletRun Run() => new()
    {
        Scope = new ReasonletScope("scope"),
        Id = ReasonletId.New(),
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), 0.5),
        Provenance = TestData.Provenance(),
    };

    private static ReasonletEvaluationRequest Request() => new()
    {
        Graph = TestData.Graph(),
        Name = "name",
        Intent = "intent",
        Question = "question",
        Answer = "answer",
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), null),
    };

    // ---- ReasonletRun ----

    [Fact]
    public void Run_WithoutQuestionAndAnswer_Passes() => Assert.True(Run().Validate().IsSuccess);

    [Fact]
    public void Run_WithQuestionAndAnswer_Passes() =>
        Assert.True((Run() with { Question = "q", Answer = "a" }).Validate().IsSuccess);

    [Fact]
    public void Run_TextAtTheCap_Passes()
    {
        // A surrogate pair counts as one code point, so this is exactly at the cap in code points but longer in chars.
        var atCap = new string('x', MaxText - 1) + "\U0001F600";
        Assert.True((Run() with { Question = atCap, Answer = atCap }).Validate().IsSuccess);
    }

    [Fact]
    public void Run_InvalidScope_ReportsNestedPath() =>
        AssertInvalid((Run() with { Scope = new ReasonletScope(" ") }).Validate(), "Scope.Value");

    [Fact]
    public void Run_DefaultId_Fails() =>
        AssertInvalid((Run() with { Id = default }).Validate(), "Id");

    [Fact]
    public void Run_InvalidOutcome_ReportsNestedPath() =>
        AssertInvalid((Run() with { Outcome = new ReasonletOutcome(true, TimeSpan.FromTicks(-1), null) }).Validate(), "Outcome.Duration");

    [Fact]
    public void Run_NullProvenance_Fails() =>
        AssertInvalid((Run() with { Provenance = null! }).Validate(), "Provenance");

    [Fact]
    public void Run_InvalidProvenance_ReportsNestedPath() =>
        AssertInvalid((Run() with { Provenance = new ReasonletProvenance { Source = " " } }).Validate(), "Provenance.Source");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Run_BlankQuestion_Fails(string question) =>
        AssertInvalid((Run() with { Question = question, Answer = "a" }).Validate(), "Question");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Run_BlankAnswer_Fails(string answer) =>
        AssertInvalid((Run() with { Question = "q", Answer = answer }).Validate(), "Answer");

    [Fact]
    public void Run_QuestionTooLong_Fails() =>
        AssertInvalid((Run() with { Question = new string('x', MaxText + 1), Answer = "a" }).Validate(), "Question");

    [Fact]
    public void Run_AnswerTooLong_Fails() =>
        AssertInvalid((Run() with { Question = "q", Answer = new string('x', MaxText + 1) }).Validate(), "Answer");

    [Fact]
    public void Run_QuestionWithALoneSurrogate_Fails() =>
        AssertInvalid((Run() with { Question = LoneSurrogate, Answer = "a" }).Validate(), "Question");

    [Fact]
    public void Run_AnswerWithALoneSurrogate_Fails() =>
        AssertInvalid((Run() with { Question = "q", Answer = LoneSurrogate }).Validate(), "Answer");

    [Fact]
    public void Run_QuestionWithoutAnswer_Fails() =>
        AssertInvalid((Run() with { Question = "q" }).Validate(), "Answer");

    [Fact]
    public void Run_AnswerWithoutQuestion_Fails() =>
        AssertInvalid((Run() with { Answer = "a" }).Validate(), "Question");

    [Fact]
    public void Run_TheErrorNeverQuotesTheText()
    {
        var result = (Run() with { Question = "q", Answer = "SECRET " + new string('x', MaxText) }).Validate();
        Assert.DoesNotContain("SECRET", result.Error.Message, StringComparison.Ordinal);
    }

    // ---- ReasonletEvaluationRequest ----

    [Fact]
    public void Request_Valid_Passes() => Assert.True(Request().Validate().IsSuccess);

    [Fact]
    public void Request_AnswerAtTheCap_Passes() =>
        Assert.True((Request() with { Answer = new string('x', MaxText) }).Validate().IsSuccess);

    [Fact]
    public void Request_LongQuestion_Passes() =>
        // A remember judges the capture's question, which has no cap, so neither does the request's.
        Assert.True((Request() with { Question = new string('x', MaxText + 1) }).Validate().IsSuccess);

    [Fact]
    public void Request_NullGraph_Fails() =>
        AssertInvalid((Request() with { Graph = null! }).Validate(), "Graph");

    [Fact]
    public void Request_InvalidGraph_ReportsNestedPath()
    {
        var graph = new ReasonletGraph { Nodes = [new ReasonletNode { Id = " ", Type = "step" }], Edges = [], SchemaVersion = 1 };
        AssertInvalid((Request() with { Graph = graph }).Validate(), "Graph.Nodes[0].Id");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Request_BlankName_Fails(string? name) =>
        AssertInvalid((Request() with { Name = name! }).Validate(), "Name");

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Request_BlankIntent_Fails(string? intent) =>
        AssertInvalid((Request() with { Intent = intent! }).Validate(), "Intent");

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Request_BlankQuestion_Fails(string? question) =>
        AssertInvalid((Request() with { Question = question! }).Validate(), "Question");

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Request_BlankAnswer_Fails(string? answer) =>
        AssertInvalid((Request() with { Answer = answer! }).Validate(), "Answer");

    [Fact]
    public void Request_AnswerTooLong_Fails() =>
        AssertInvalid((Request() with { Answer = new string('x', MaxText + 1) }).Validate(), "Answer");

    [Fact]
    public void Request_AnswerWithALoneSurrogate_Fails() =>
        AssertInvalid((Request() with { Answer = LoneSurrogate }).Validate(), "Answer");

    [Fact]
    public void Request_InvalidOutcome_ReportsNestedPath() =>
        AssertInvalid((Request() with { Outcome = new ReasonletOutcome(true, TimeSpan.Zero, 2) }).Validate(), "Outcome.Score");

    // ---- ReasonletEvaluation ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Evaluation_ScoreInRange_Passes(double score) =>
        Assert.True(new ReasonletEvaluation(true, score, "llm-judge").Validate().IsSuccess);

    [Fact]
    public void Evaluation_EvaluatorAtTheCap_Passes() =>
        Assert.True(new ReasonletEvaluation(false, 0.5, new string('e', 100)).Validate().IsSuccess);

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Evaluation_ScoreOutOfRange_Fails(double score) =>
        AssertInvalid(new ReasonletEvaluation(true, score, "llm-judge").Validate(), "Score");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Evaluation_BlankEvaluator_Fails(string? evaluator) =>
        AssertInvalid(new ReasonletEvaluation(true, 0.5, evaluator!).Validate(), "Evaluator");

    [Fact]
    public void Evaluation_EvaluatorTooLong_Fails() =>
        AssertInvalid(new ReasonletEvaluation(true, 0.5, new string('e', 101)).Validate(), "Evaluator");

    [Fact]
    public void Evaluation_EvaluatorWithALoneSurrogate_Fails() =>
        AssertInvalid(new ReasonletEvaluation(true, 0.5, LoneSurrogate).Validate(), "Evaluator");

    [Fact]
    public void Evaluation_Default_IsInvalid() =>
        AssertInvalid(default(ReasonletEvaluation).Validate(), "Evaluator");

    // ---- ReasonletCapture.Answer ----

    [Fact]
    public void Capture_WithAnswer_Passes() =>
        Assert.True((TestData.Capture() with { Answer = "answer" }).Validate().IsSuccess);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Capture_BlankAnswer_Fails(string answer) =>
        AssertInvalid((TestData.Capture() with { Answer = answer }).Validate(), "Answer");

    [Fact]
    public void Capture_AnswerTooLong_Fails() =>
        AssertInvalid((TestData.Capture() with { Answer = new string('x', MaxText + 1) }).Validate(), "Answer");

    [Fact]
    public void Capture_AnswerAtTheCap_Passes() =>
        Assert.True((TestData.Capture() with { Answer = new string('x', MaxText) }).Validate().IsSuccess);

    [Fact]
    public void Capture_AnswerWithALoneSurrogate_Fails() =>
        AssertInvalid((TestData.Capture() with { Answer = LoneSurrogate }).Validate(), "Answer");

    // ---- ReasonletFeedback.Worked ----

    [Fact]
    public void Feedback_WorkedDefaultsToNull()
    {
        Assert.Null(new ReasonletFeedback(0.1, 0.2, 0.3).Worked);
        Assert.Null(default(ReasonletFeedback).Worked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Feedback_Worked_IsKeptAndValid(bool worked)
    {
        var feedback = new ReasonletFeedback(0.1, 0.2, 0.3, worked);
        Assert.Equal(worked, feedback.Worked);
        Assert.True(feedback.Validate().IsSuccess);
    }

    [Fact]
    public void Feedback_WorkedDoesNotHideAnInvalidScore() =>
        AssertInvalid(new ReasonletFeedback(2, 0, 0, true).Validate(), "Utility");

    // ---- ReasonletStoreEntry ----

    [Fact]
    public void StoreEntry_EvaluationDefaultsToNull() => Assert.Null(TestData.StoreEntry().Evaluation);

    [Fact]
    public void StoreEntry_WithAValidEvaluation_Passes() =>
        Assert.True((TestData.StoreEntry() with { Evaluation = new ReasonletEvaluation(true, 0.9, "llm-judge") }).Validate().IsSuccess);

    [Fact]
    public void StoreEntry_InvalidEvaluation_ReportsNestedPath() =>
        AssertInvalid((TestData.StoreEntry() with { Evaluation = new ReasonletEvaluation(true, 1.5, "llm-judge") }).Validate(), "Evaluation.Score");

    [Fact]
    public void StoreEntry_CaptureWithAnAnswer_Fails() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Answer = "answer" } }).Validate(), "Capture.Answer");

    [Fact]
    public void StoreEntry_InvalidCaptureAnswer_ReportsTheCaptureRuleFirst() =>
        AssertInvalid((TestData.StoreEntry() with { Capture = TestData.Capture() with { Answer = " " } }).Validate(), "Capture.Answer");
}
