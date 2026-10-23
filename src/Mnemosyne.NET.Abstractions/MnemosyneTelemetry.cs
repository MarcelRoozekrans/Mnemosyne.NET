namespace Mnemosyne.NET.Abstractions;

/// <summary>The names under which Mnemosyne emits OpenTelemetry signals.</summary>
/// <remarks>
/// A host enables them with <c>.AddSource(MnemosyneTelemetry.ActivitySourceName)</c> on its tracer provider and
/// <c>.AddMeter(MnemosyneTelemetry.MeterName)</c> on its meter provider. No signal carries user data: no scope,
/// subject, question, intent, name, graph, tags or run id. Ids and versions are opaque, so they may appear.
/// </remarks>
public static class MnemosyneTelemetry
{
    /// <summary>The name of the <c>ActivitySource</c> that traces every store operation.</summary>
    public const string ActivitySourceName = "Mnemosyne.NET";

    /// <summary>The name of the <c>Meter</c> that counts and times every store operation.</summary>
    public const string MeterName = "Mnemosyne.NET";
}
