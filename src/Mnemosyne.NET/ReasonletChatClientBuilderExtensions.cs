using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Adds the Reasonlet middleware to a chat client pipeline.</summary>
public static class ReasonletChatClientBuilderExtensions
{
    private const string MissingQueue =
        "UseReasonlets captures by default and needs services.AddReasonletCapture(); call it, or set ReasonletChatOptions.Capture = false.";

    private const string MissingMemory = "UseReasonlets needs an IReasonletMemory; register one, for example with services.AddMnemosyne().";

    /// <summary>
    /// Adds the middleware that recalls a known procedure for each new question and offers it to the model, can expose the
    /// <c>find_procedure</c> tool, and hands each finished call to the background capture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Place it outside <c>UseFunctionInvocation()</c>, so that one call to it spans the whole tool loop; inside, the
    /// injected parent and the <c>find_procedure</c> lookups are lost, because only the last trip of the loop is captured as a
    /// finished run and it is a continuation without an inject.
    /// </para>
    /// <para>
    /// When the pipeline is built, it resolves the registered <see cref="TimeProvider"/> or <see cref="TimeProvider.System"/>,
    /// and checks that <see cref="IReasonletMemory"/> and, when <see cref="ReasonletChatOptions.Capture"/> is on, the capture
    /// queue that <c>AddReasonletCapture</c> registers are registered. It resolves the memory and the queue on first use, so
    /// a memory whose evaluator judges with this same client, as <c>AddMnemosyneChatEvaluator</c> does with the unkeyed
    /// client, does not form a cycle. A container without <see cref="IServiceProviderIsService"/> cannot be asked, so there
    /// both checks are skipped and the memory and the queue are still resolved on first use: a missing memory then counts as
    /// <c>exception</c> on <c>mnemosyne.chat.recalls</c>, and a missing queue as <c>exception</c> on
    /// <c>mnemosyne.capture.queue</c>, on each call, and neither breaks the call.
    /// </para>
    /// <para>
    /// A memory or queue whose resolution throws on a call never breaks that call. A memory that cannot be resolved counts
    /// once as <c>exception</c> on <c>mnemosyne.chat.recalls</c>, and the call goes on without recall, inject and
    /// <c>find_procedure</c>; this holds with <see cref="ReasonletChatOptions.Recall"/> off and the tool on too. A queue that
    /// cannot be resolved counts as <c>exception</c> on <c>mnemosyne.capture.queue</c>. A failed resolution is not cached:
    /// the next call tries again. The caller's own cancellation still propagates.
    /// </para>
    /// <para>
    /// A judge call from <see cref="ChatReasonletEvaluator"/> carries an internal mark, and the middleware passes it straight
    /// through: it is not recalled for, gets no tool, is not captured and is not counted.
    /// </para>
    /// </remarks>
    /// <param name="builder">The pipeline builder.</param>
    /// <param name="options">The settings, or null for the defaults.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the pipeline is built without a registered <see cref="IReasonletMemory"/>, or with capture on and
    /// <c>AddReasonletCapture</c> not called. Only a container with <see cref="IServiceProviderIsService"/> is checked.
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
            // The memory and the queue are resolved on the first call, not here: the memory can depend on this very
            // client, as the chat evaluator does, and resolving it while the client is built would never finish.
            var registry = services.GetService<IServiceProviderIsService>();
            if (registry is not null && !registry.IsService(typeof(IReasonletMemory)))
            {
                throw new InvalidOperationException(MissingMemory);
            }

            var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var memory = new Lazy<IReasonletMemory>(services.GetRequiredService<IReasonletMemory>, LazyThreadSafetyMode.PublicationOnly);
            Lazy<ReasonletCaptureQueue?> queue;
            if (!options.Capture)
            {
                queue = new Lazy<ReasonletCaptureQueue?>(() => null);
            }
            else
            {
                // A container that cannot say whether a service is registered skips the check: resolving the queue here
                // would bring the cycle back, so a missing queue shows on the first call, counted by the hand-off.
                if (registry is not null && !registry.IsService(typeof(ReasonletCaptureQueue)))
                {
                    throw new InvalidOperationException(MissingQueue);
                }

                queue = new Lazy<ReasonletCaptureQueue?>(services.GetRequiredService<ReasonletCaptureQueue>, LazyThreadSafetyMode.PublicationOnly);
            }

            return new ReasonletChatClient(inner, memory, queue, options, time);
        });
    }
}
