using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Adds the Reasonlet middleware to a chat client pipeline.</summary>
public static class ReasonletChatClientBuilderExtensions
{
    private const string MissingQueue =
        "UseReasonlets captures by default and needs services.AddReasonletCapture(); call it, or set ReasonletChatOptions.Capture = false.";

    /// <summary>
    /// Adds the middleware that recalls a known procedure for each new question and offers it to the model, can expose the
    /// <c>find_procedure</c> tool, and hands each finished call to the background capture.
    /// </summary>
    /// <remarks>
    /// Place it outside <c>UseFunctionInvocation()</c>, so that one call to it spans the whole tool loop; inside, each trip
    /// to the model is captured on its own, and the one that only asks for a tool is skipped for having no tool calls. When the pipeline is built, it resolves <see cref="IReasonletMemory"/>,
    /// the registered <see cref="TimeProvider"/> or <see cref="TimeProvider.System"/>, and, when
    /// <see cref="ReasonletChatOptions.Capture"/> is on, the capture queue that <c>AddReasonletCapture</c> registers.
    /// </remarks>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the pipeline is built with capture on and <c>AddReasonletCapture</c> was not called.
    /// </exception>
    public static ChatClientBuilder UseReasonlets(this ChatClientBuilder builder, ReasonletChatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new ReasonletChatOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        return builder.Use((inner, services) =>
        {
            var memory = services.GetRequiredService<IReasonletMemory>();
            var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var queue = options.Capture
                ? services.GetService<ReasonletCaptureQueue>() ?? throw new InvalidOperationException(MissingQueue)
                : null;
            return new ReasonletChatClient(inner, memory, queue, options, time);
        });
    }
}
