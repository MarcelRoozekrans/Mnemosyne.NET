using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The public count of a finished run that could not be handed to capture.</summary>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletCapturesTests
{
    [Fact]
    public void CountFailedCountsTheExceptionType()
    {
        using var telemetry = new TelemetryCapture();

        ReasonletCaptures.CountFailed(new InvalidOperationException("secret detail"));

        var count = Assert.Single(telemetry.Of("mnemosyne.capture.queue"));
        Assert.Equal("exception", count.Tag("mnemosyne.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, count.Tag("error.type"));
        Assert.DoesNotContain("secret", string.Join(' ', count.Tags.Select(tag => tag.Value?.ToString())));
    }

    [Fact]
    public void CountFailedRejectsANullException() =>
        Assert.Throws<ArgumentNullException>(() => ReasonletCaptures.CountFailed(null!));
}
