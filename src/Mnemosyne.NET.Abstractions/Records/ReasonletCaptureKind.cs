namespace Mnemosyne.NET.Abstractions;

/// <summary>Describes what a capture produced.</summary>
public enum ReasonletCaptureKind
{
    /// <summary>A new Reasonlet was created.</summary>
    NewReasonlet,

    /// <summary>A new version of an existing Reasonlet was created.</summary>
    NewVersion,

    /// <summary>Only an execution was recorded against an existing version.</summary>
    ExecutionOnly,
}
