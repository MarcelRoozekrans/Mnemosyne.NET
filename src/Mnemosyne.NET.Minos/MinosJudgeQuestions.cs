using Minos;

namespace Mnemosyne.NET.Minos;

/// <summary>The one question the Minos judge asks about a run.</summary>
/// <remarks>Minos's source generator writes the question definition and the answer parsing at compile time.</remarks>
[Questions(State = typeof(MinosJudgeState))]
internal sealed partial class MinosJudgeQuestions
{
    /// <summary>Gets whether the answer resolves the question by following the procedure.</summary>
    [Noul("Does the answer resolve the question by following the procedure?")]
    public partial Noul Resolves { get; }
}
