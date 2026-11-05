using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>The tools Mnemosyne offers a chat model.</summary>
public static class ReasonletTools
{
    /// <summary>The name of the procedure lookup tool.</summary>
    public const string FindProcedureName = "find_procedure";

    /// <summary>Creates the tool that lets a model look up procedures that worked before.</summary>
    /// <param name="memory">The memory to recall from.</param>
    /// <param name="scope">The scope every lookup runs in.</param>
    /// <returns>The tool, named <see cref="FindProcedureName"/>. Its <see cref="FindProcedureFunction.Lookups"/> reports the matches of each lookup.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/> is <see langword="null"/>.</exception>
    public static FindProcedureFunction CreateFindProcedure(IReasonletMemory memory, ReasonletScope scope)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new FindProcedureFunction(memory, scope);
    }
}
