using System.Reflection;
using System.Xml.Linq;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Abstractions;

public class ContractShapeTests
{
    private static readonly Type[] Contracts =
    [
        typeof(IReasonletMemory),
        typeof(IReasonletStore),
        typeof(IReasonletSelector),
        typeof(IIntentExtractor),
        typeof(IReasonletEvaluator),
    ];

    [Fact]
    public void EveryAsyncMemberFollowsTheHouseStyle()
    {
        foreach (var contract in Contracts)
        {
            // Property accessors are checked by TheStoreExposesItsDimensionsReadOnly.
            var methods = contract.GetMethods().Where(m => !m.IsSpecialName).ToArray();
            Assert.NotEmpty(methods);
            foreach (var m in methods)
            {
                var label = contract.Name + "." + m.Name;
                Assert.True(m.Name.EndsWith("Async", StringComparison.Ordinal), label + " must end with Async");

                var rt = m.ReturnType;
                Assert.True(rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>), label + " must return ValueTask<>");
                var inner = rt.GetGenericArguments()[0];
                Assert.True(inner.IsGenericType, label + " must wrap a ZeroAlloc result");
                var def = inner.GetGenericTypeDefinition();
                var args = inner.GetGenericArguments();
                if (def == typeof(Result<,>))
                {
                    Assert.Equal(typeof(MnemosyneError), args[1]);
                }
                else
                {
                    Assert.Equal(typeof(UnitResult<>), def);
                    Assert.Equal(typeof(MnemosyneError), args[0]);
                }

                var parameters = m.GetParameters();
                Assert.True(parameters.Length > 0, label + " must take a CancellationToken ct");
                var last = parameters[^1];
                Assert.True(last.ParameterType == typeof(CancellationToken), label + " last parameter must be a CancellationToken");
                Assert.True(last.Name == "ct", label + " last parameter must be named ct");
                Assert.False(last.HasDefaultValue, label + " ct must have no default");
            }
        }
    }

    [Theory]
    [InlineData(typeof(ReasonletCapture))]
    [InlineData(typeof(ReasonletNode))]
    [InlineData(typeof(ReasonletEdge))]
    [InlineData(typeof(ReasonletGraph))]
    [InlineData(typeof(ReasonletDefinition))]
    [InlineData(typeof(ReasonletProvenance))]
    [InlineData(typeof(ReasonletQuery))]
    [InlineData(typeof(ReasonletErasureResult))]
    [InlineData(typeof(ReasonletRetention))]
    [InlineData(typeof(ReasonletPruneResult))]
    [InlineData(typeof(ReasonletRun))]
    [InlineData(typeof(ReasonletEvaluationRequest))]
    public void GrowableRecordsAreInitOnly(Type record)
    {
        var constructors = record.GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
    }

    [Fact]
    public void TheStoreExposesItsDimensionsReadOnly()
    {
        var properties = typeof(IReasonletStore).GetProperties();
        var dimensions = Assert.Single(properties);
        Assert.Equal("Dimensions", dimensions.Name);
        Assert.Equal(typeof(int), dimensions.PropertyType);
        Assert.True(dimensions.CanRead);
        Assert.False(dimensions.CanWrite);
        foreach (var contract in Contracts.Where(c => c != typeof(IReasonletStore)))
        {
            Assert.Empty(contract.GetProperties());
        }
    }

    private static string[] Signature(MethodInfo method) =>
        [.. method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)];

    [Fact]
    public void RecordExecutionTakesOneRun()
    {
        var method = typeof(IReasonletMemory).GetMethod("RecordExecutionAsync")!;
        Assert.Equal(["ReasonletRun run", "CancellationToken ct"], Signature(method));
        Assert.Equal(typeof(ValueTask<Result<ReasonletRunReceipt, MnemosyneError>>), method.ReturnType);
    }

    [Fact]
    public void TheRunReceiptIsAPositionalRecordOfTheExecutionAndItsVerdict()
    {
        Assert.False(typeof(ReasonletRunReceipt).IsValueType);
        var constructor = Assert.Single(typeof(ReasonletRunReceipt).GetConstructors(), c => c.GetParameters().Length > 1);
        Assert.Equal(
            ["ExecutionId Execution", "ReasonletVerdictSource Source", "Boolean Success"],
            constructor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
    }

    [Fact]
    public void TheReceiptEndsWithTheVerdictOfItsRun()
    {
        var constructor = Assert.Single(typeof(ReasonletReceipt).GetConstructors(), c => c.GetParameters().Length > 1);
        Assert.Equal(
            [
                "ReasonletId Id",
                "String Version",
                "ExecutionId Execution",
                "ReasonletCaptureKind Kind",
                "ReasonletStatus Status",
                "ReasonletVerdictSource Source",
                "Boolean Success",
            ],
            constructor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
    }

    [Fact]
    public void AppendExecutionTakesTheEvaluationLast()
    {
        var method = typeof(IReasonletStore).GetMethod("AppendExecutionAsync")!;
        Assert.Equal(
            [
                "ReasonletScope scope",
                "ReasonletId id",
                "ReasonletOutcome outcome",
                "ReasonletProvenance provenance",
                "Nullable`1 evaluation",
                "CancellationToken ct",
            ],
            Signature(method));
        Assert.Equal(typeof(ReasonletEvaluation?), method.GetParameters()[4].ParameterType);
        Assert.Equal(typeof(ValueTask<Result<ReasonletRunReceipt, MnemosyneError>>), method.ReturnType);
    }

    private static string[] PositionalShape(Type record) =>
        [.. record.GetConstructors().Single(c => c.GetParameters().Length > 1).GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)];

    [Fact]
    public void TheMatchCarriesTheEvidenceBehindItsScore()
    {
        Assert.False(typeof(ReasonletMatch).IsValueType);
        Assert.Equal(
            ["ReasonletDefinition Reasonlet", "ReasonletScore Score", "ReasonletEvidence Evidence"],
            PositionalShape(typeof(ReasonletMatch)));
    }

    [Fact]
    public void TheEvidenceIsAPositionalValue()
    {
        Assert.True(typeof(ReasonletEvidence).IsValueType);
        Assert.True(typeof(ReasonletEvidence).IsPublic);
        Assert.Equal("Mnemosyne.NET.Abstractions", typeof(ReasonletEvidence).Namespace);
        Assert.Equal(["Int32 Runs", "Int32 JudgedRuns", "Boolean UsesParentPrior"], PositionalShape(typeof(ReasonletEvidence)));
    }

    [Fact]
    public void TheMetricsEndWithOptionalJudgedCounters()
    {
        Assert.True(typeof(ReasonletMetrics).IsValueType);
        Assert.Equal(
            ["Int32 Runs", "Int32 Successes", "Nullable`1 LastSuccessAt", "DateTimeOffset LastExecutedAt", "Int32 JudgedRuns", "Int32 JudgedSuccesses"],
            PositionalShape(typeof(ReasonletMetrics)));
        var parameters = typeof(ReasonletMetrics).GetConstructors().Single(c => c.GetParameters().Length > 1).GetParameters();
        foreach (var judged in parameters[^2..])
        {
            Assert.True(judged.HasDefaultValue);
            Assert.Equal(0, judged.DefaultValue);
        }
    }

    [Fact]
    public void AppendFeedbackKeepsItsSignature() =>
        Assert.Equal(
            ["ReasonletScope scope", "ExecutionId execution", "ReasonletFeedback feedback", "CancellationToken ct"],
            Signature(typeof(IReasonletStore).GetMethod("AppendFeedbackAsync")!));

    [Fact]
    public void TheEvaluatorJudgesOneRequest()
    {
        var method = Assert.Single(typeof(IReasonletEvaluator).GetMethods());
        Assert.Equal("EvaluateAsync", method.Name);
        Assert.Equal(["ReasonletEvaluationRequest request", "CancellationToken ct"], Signature(method));
        Assert.Equal(typeof(ValueTask<Result<ReasonletEvaluation, MnemosyneError>>), method.ReturnType);
        Assert.True(typeof(IReasonletEvaluator).IsPublic);
        Assert.Equal("Mnemosyne.NET.Abstractions", typeof(IReasonletEvaluator).Namespace);
    }

    [Fact]
    public void TheEvaluationRequestCarriesTheRunAndItsProcedure()
    {
        var properties = typeof(ReasonletEvaluationRequest).GetProperties()
            .Select(p => p.PropertyType.Name + " " + p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["ReasonletGraph Graph", "ReasonletOutcome Outcome", "String Answer", "String Intent", "String Name", "String Question"],
            properties);
    }

    [Fact]
    public void TheEvaluationIsAPositionalValue()
    {
        Assert.True(typeof(ReasonletEvaluation).IsValueType);
        var constructor = Assert.Single(typeof(ReasonletEvaluation).GetConstructors(), c => c.GetParameters().Length > 0);
        Assert.Equal(["Boolean Success", "Double Score", "String Evaluator"], constructor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
    }

    [Fact]
    public void TheRunCarriesAnOptionalQuestionAndAnswer()
    {
        var properties = typeof(ReasonletRun).GetProperties()
            .Select(p => p.PropertyType.Name + " " + p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "ReasonletId Id",
                "ReasonletOutcome Outcome",
                "ReasonletProvenance Provenance",
                "ReasonletScope Scope",
                "String Answer",
                "String Question",
            ],
            properties);
        var run = new ReasonletRun { Scope = new ReasonletScope("s"), Id = ReasonletId.New(), Outcome = default, Provenance = TestData.Provenance() };
        Assert.Null(run.Question);
        Assert.Null(run.Answer);
    }

    [Fact]
    public void FeedbackTakesAnOptionalWorkedVerdictLast()
    {
        var parameters = Assert.Single(typeof(ReasonletFeedback).GetConstructors(), c => c.GetParameters().Length > 0).GetParameters();
        var worked = parameters[^1];
        Assert.Equal(4, parameters.Length);
        Assert.Equal("Worked", worked.Name);
        Assert.Equal(typeof(bool?), worked.ParameterType);
        Assert.True(worked.HasDefaultValue);
        Assert.Null(worked.DefaultValue);
    }

    [Fact]
    public void TheStoreEntryTakesAnOptionalEvaluationThenReviewRequiredLast()
    {
        var parameters = Assert.Single(typeof(ReasonletStoreEntry).GetConstructors(), c => c.GetParameters().Length > 1).GetParameters();
        var evaluation = parameters[^2];
        Assert.Equal("Evaluation", evaluation.Name);
        Assert.Equal(typeof(ReasonletEvaluation?), evaluation.ParameterType);
        Assert.True(evaluation.HasDefaultValue);
        Assert.Null(evaluation.DefaultValue);
        var review = parameters[^1];
        Assert.Equal("ReviewRequired", review.Name);
        Assert.Equal(typeof(bool), review.ParameterType);
        Assert.True(review.HasDefaultValue);
        Assert.Equal(false, review.DefaultValue);
    }

    [Fact]
    public void ReasonletQueryHasNoProvenance() =>
        Assert.Null(typeof(ReasonletQuery).GetProperty("Provenance"));

    [Theory]
    [InlineData(typeof(IReasonletMemory))]
    [InlineData(typeof(IReasonletStore))]
    public void EraseAsyncReturnsAnErasureResult(Type contract) =>
        Assert.Equal(
            typeof(ValueTask<Result<ReasonletErasureResult, MnemosyneError>>),
            contract.GetMethod("EraseAsync")!.ReturnType);

    [Fact]
    public void ErasureResult_CarriesCounts()
    {
        var result = new ReasonletErasureResult { Reasonlets = 2, Executions = 5 };
        Assert.Equal(2, result.Reasonlets);
        Assert.Equal(5, result.Executions);

        // Init-only so later phases can add members; a with-expression copies the rest.
        var copy = result with { Executions = 6 };
        Assert.Equal(2, copy.Reasonlets);
        Assert.Equal(6, copy.Executions);
    }

    [Fact]
    public void PruneAsyncReturnsAPruneResult() =>
        Assert.Equal(
            typeof(ValueTask<Result<ReasonletPruneResult, MnemosyneError>>),
            typeof(IReasonletStore).GetMethod("PruneAsync")!.ReturnType);

    [Fact]
    public void PruneResult_CarriesCounts()
    {
        var result = new ReasonletPruneResult { Reasonlets = 2, Executions = 5 };
        Assert.Equal(2, result.Reasonlets);
        Assert.Equal(5, result.Executions);

        var copy = result with { Executions = 6 };
        Assert.Equal(2, copy.Reasonlets);
        Assert.Equal(6, copy.Executions);
    }

    [Fact]
    public void CheckExposesOnlyTheHelpersOtherPackagesUse()
    {
        // Public API: a change here is a breaking or widening change to every package that extends Mnemosyne.
        var members = typeof(Check).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "All(ReadOnlySpan`1)",
                "Fail(String, String)",
                "MaxLength(String, Int32, String)",
                "Nested(UnitResult`1, String)",
                "NotBlank(String, String)",
                "NotDefault(ExecutionId, String)",
                "NotDefault(ReasonletId, String)",
                "NotNull(Object, String)",
                "Range(Int32, Int32, Int32, String)",
                "UnitInterval(Double, String)",
                "ValidUtf16(String, String)",
            ],
            members);
        Assert.True(typeof(Check).IsPublic);
        Assert.Equal("Mnemosyne.NET.Abstractions.Validation", typeof(Check).Namespace);
    }

    [Fact]
    public void OnlyTheTestProjectSeesAbstractionsInternals()
    {
        var csproj = Path.Combine(RepoConventionsTests.RepoRoot(), "src", "Mnemosyne.NET.Abstractions", "Mnemosyne.NET.Abstractions.csproj");
        var friends = XDocument.Load(csproj).Descendants("InternalsVisibleTo")
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();
        Assert.Equal(new string?[] { "Mnemosyne.NET.Tests" }, friends);
    }

    [Fact]
    public void AbstractionsReferencesOnlyZeroAllocResults()
    {
        var csproj = Path.Combine(RepoConventionsTests.RepoRoot(), "src", "Mnemosyne.NET.Abstractions", "Mnemosyne.NET.Abstractions.csproj");
        var packages = XDocument.Load(csproj).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .ToArray();
        Assert.Equal(new string?[] { "ZeroAlloc.Results" }, packages);
    }
}
