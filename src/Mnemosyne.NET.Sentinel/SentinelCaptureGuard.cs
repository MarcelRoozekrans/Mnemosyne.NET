using System.Text;
using AI.Sentinel;
using AI.Sentinel.Detection;
using AI.Sentinel.Domain;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Sentinel;

/// <summary>
/// An <see cref="ICaptureGuard"/> that runs AI.Sentinel's detection pipeline over a capture's question, intent and name,
/// and blocks the capture when a detector reaches <see cref="SentinelCaptureGuardOptions.BlockAtSeverity"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline sees the question, the intent and the name, one user message each in that order, and nothing else: not the
/// answer, the graph or the provenance tags. The session is the run id of the capture's provenance, or
/// <c>mnemosyne-capture</c> when it has none. The sender and receiver are the defaults of the <see cref="SentinelOptions"/>
/// the host configured.
/// </para>
/// <para>
/// A blocked capture is named by the blocking detection with the highest severity; on a tie, the first one wins. The
/// detection's reason is never read, since it can quote the capture. Exceptions from the pipeline and cancellation
/// propagate; the guard never returns an error.
/// </para>
/// </remarks>
public sealed class SentinelCaptureGuard : ICaptureGuard
{
    private const string FallbackSessionId = "mnemosyne-capture";
    private const string InvalidDetectorId = "invalid-detector-id";

    private readonly IDetectionPipeline _pipeline;
    private readonly AgentId _sender;
    private readonly AgentId _receiver;
    private readonly SentinelCaptureGuardOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SentinelCaptureGuard"/> class.</summary>
    /// <param name="pipeline">The AI.Sentinel detection pipeline to run.</param>
    /// <param name="sentinelOptions">AI.Sentinel's options, which supply the default sender and receiver.</param>
    /// <param name="options">The guard's settings, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> or <paramref name="sentinelOptions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> fails <see cref="SentinelCaptureGuardOptions.Validate"/>.</exception>
    public SentinelCaptureGuard(IDetectionPipeline pipeline, SentinelOptions sentinelOptions, SentinelCaptureGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(sentinelOptions);
        options ??= new SentinelCaptureGuardOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _pipeline = pipeline;
        _sender = sentinelOptions.DefaultSenderId;
        _receiver = sentinelOptions.DefaultReceiverId;
        _options = options;
    }

    /// <inheritdoc/>
    public async ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var messages = new List<ChatMessage>(3) { new(ChatRole.User, capture.Question) };
        AddIfPresent(messages, capture.Intent);
        AddIfPresent(messages, capture.Name);

        var runId = capture.Provenance.RunId;
        var context = new SentinelContext(
            _sender, _receiver, new SessionId(string.IsNullOrWhiteSpace(runId) ? FallbackSessionId : runId), messages, []);

        var result = await _pipeline.RunAsync(context, ct).ConfigureAwait(false);

        DetectionResult? blocking = null;
        foreach (var detection in result.Detections)
        {
            if (detection.Severity >= _options.BlockAtSeverity && (blocking is null || detection.Severity > blocking.Severity))
            {
                blocking = detection;
            }
        }

        return Result<CaptureVerdict, MnemosyneError>.Success(
            blocking is null
                ? CaptureVerdict.Allow(_options.GuardName)
                : CaptureVerdict.Block(_options.GuardName, DetectorName(blocking.DetectorId.Value)));
    }

    private static void AddIfPresent(List<ChatMessage> messages, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            messages.Add(new ChatMessage(ChatRole.User, text));
        }
    }

    /// <summary>Fits a detector id to the verdict: cut to the code point limit, or replaced when it is not valid UTF-16.</summary>
    private static string DetectorName(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Check.ValidUtf16(id, nameof(id)).IsFailure)
        {
            return InvalidDetectorId;
        }

        var end = 0;
        var count = 0;
        foreach (var rune in id.EnumerateRunes())
        {
            if (count == EvaluationLimits.MaxEvaluatorLength)
            {
                return id[..end];
            }

            end += rune.Utf16SequenceLength;
            count++;
        }

        return id;
    }
}
