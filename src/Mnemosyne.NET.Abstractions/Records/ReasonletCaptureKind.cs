namespace Mnemosyne.NET.Abstractions;

/// <summary>Describes what a capture produced.</summary>
/// <remarks>The values are numbered from 1 because stores persist them; 0 is undefined.</remarks>
public enum ReasonletCaptureKind
{
    /// <summary>A new Reasonlet was created.</summary>
    NewReasonlet = 1,

    /// <summary>A new version of an existing Reasonlet was created.</summary>
    NewVersion = 2,

    /// <summary>Only an execution was recorded against an existing version.</summary>
    ExecutionOnly = 3,
}
