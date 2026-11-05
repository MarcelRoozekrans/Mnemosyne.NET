using AI.Sentinel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.NET.Sentinel;
using Mnemosyne.Samples;
using Npgsql;
using ZeroAlloc.Results;

// Runs a chat pipeline offline and shows what Mnemosyne does with each exchange. A scripted client stands in for the
// model, so the sample needs no API key. The pipeline is UseReasonlets over UseFunctionInvocation. AI.Sentinel guards
// what is stored, and the review gate holds each new Reasonlet back until a person approves it.
// The output reads as a walkthrough in three acts. The sample checks its own receipts and exits 1 if they differ, so
// what the docs say about the chat pipeline is tested in CI.
//
// Needs PostgreSQL 15 or later with pgvector 0.8.0 or later: `docker compose up -d --wait` next to this file starts
// one. MNEMOSYNE_SAMPLE_PG overrides the connection string. The sample migrates the mnemosyne schema with 256
// dimensions, so point it at a database of its own.
const string DefaultConnectionString = "Host=localhost;Port=5434;Username=postgres;Password=postgres;Database=mnemosyne_chat_sample";
const string RefundQuestion = "How do I refund an order?";
const string RefundAgainQuestion = "How do I refund an order for a customer?";
const string CardNumber = "4111 1111 1111 1111";
const string CardQuestion = $"Which invoice was paid with card {CardNumber}?";
const string RefundAnswer = "Refund REF-4821 was issued and the customer was notified by email.";
const string InvoiceAnswer = "Invoice INV-1007 was paid in full.";

var connectionString = Environment.GetEnvironmentVariable("MNEMOSYNE_SAMPLE_PG") is { Length: > 0 } configured
    ? configured
    : DefaultConnectionString;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = cts.Token;

try
{
    return await Run(connectionString, ct);
}
catch (SampleFailure failure)
{
    Console.Error.WriteLine($"FAILED: {failure.Message}");
    return 1;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("FAILED: the sample did not finish within 2 minutes.");
    return 1;
}
catch (TimeoutException ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}

static async Task<int> Run(string connectionString, CancellationToken ct)
{
    // 1. The host wiring: a data source, the PostgreSQL store, an embedding generator, the review gate, the AI.Sentinel
    // capture guard, the capture queue and the facade.
    var services = new ServiceCollection();
    services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
    services.AddAISentinel();
    services.AddMnemosyneSentinel();
    services.AddMnemosyne(new MnemosyneOptions { RequireReview = true });
    services.AddReasonletCapture();
    await using var provider = services.BuildServiceProvider();
    Console.WriteLine($"Wired the PostgreSQL store, the hashing embedding generator ({HashingEmbeddingGenerator.Dimensions} dimensions), the review gate, the AI.Sentinel capture guard and the Mnemosyne facade.");

    RequireSuccess(await provider.GetRequiredService<ReasonletPostgreSqlStore>().MigrateAsync(ct), "Migrate");
    Console.WriteLine("Migrated the mnemosyne schema.");

    var memory = provider.GetRequiredService<IReasonletMemory>();
    var scope = new ReasonletScope($"sample-{Guid.NewGuid():N}");
    Console.WriteLine($"Working in scope {scope.Value}.");

    var exitCode = 1;
    try
    {
        exitCode = await Walkthrough(provider, memory, scope, ct);
    }
    finally
    {
        // 4. Erase the scope, whatever happened above, so a run leaves no Reasonlets behind. A failed erase is
        // reported but never replaces a failure that is already on its way out.
        if (!await TryErase(memory, scope))
        {
            exitCode = 1;
        }
    }

    return exitCode;
}

// Runs in a finally block, so it must not throw: an exception here would hide the one that got the sample there.
// The erase does not take the run's token, so a timed-out run still cleans up.
static async Task<bool> TryErase(IReasonletMemory memory, ReasonletScope scope)
{
    try
    {
        var erased = await memory.EraseAsync(new ReasonletErasure(scope, null), CancellationToken.None);
        if (erased.IsFailure)
        {
            Console.Error.WriteLine($"Erase failed: {erased.Error.Code}: {erased.Error.Message}");
            return false;
        }

        Console.WriteLine($"Erased the scope: {erased.Value.Reasonlets} Reasonlets and {erased.Value.Executions} executions deleted.");
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Erase threw {ex.GetType().Name}: {ex.Message}");
        return false;
    }
}

static async Task<int> Walkthrough(IServiceProvider provider, IReasonletMemory memory, ReasonletScope scope, CancellationToken ct)
{
    // The watcher exists before the first capture starts, since it only hears captures that finish afterwards.
    using var watcher = new CaptureRunWatcher();

    var refunds = new ScriptedChatClient(
        [new ScriptedToolCall("orders_get", new Dictionary<string, object?>()), new ScriptedToolCall("payments_refund", new Dictionary<string, object?>())],
        RefundAnswer);
    var refundClient = BuildClient(refunds, provider, scope);

    Console.WriteLine();
    Console.WriteLine("The pipeline is UseReasonlets over UseFunctionInvocation. A scripted client plays the model: it calls orders_get and payments_refund, then answers.");
    Console.WriteLine();

    // Act 1: a new procedure. The review gate stores it as pending, and a pending Reasonlet is never recalled.
    Console.WriteLine($"Act 1. Ask \"{RefundQuestion}\" and let the capture run in the background.");
    var reply = await RunAsync(refundClient, RefundQuestion, ct);
    RequireEqual(reply, RefundAnswer, "Act 1: the reply");
    var outcome1 = await watcher.NextOutcomeAsync(TimeSpan.FromSeconds(30), ct);
    RequireEqual(outcome1, "new_reasonlet", "Act 1: the capture outcome");
    var pending = Require(await memory.ListPendingAsync(scope, 10, null, ct), "Act 1: list pending");
    RequireEqual(pending.Count, 1, "Act 1: the pending reviews");
    var found = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = RefundQuestion, Top = 3 }, ct), "Act 1: recall");
    RequireEqual(found.Count, 0, "Act 1: the matches for the same question");
    var name = pending[0].Definition.Name;
    var act1Injected = refunds.SystemText(0).Contains(name, StringComparison.Ordinal);
    RequireEqual(act1Injected, false, "Act 1: whether the first call carried a recalled Reasonlet");
    Console.WriteLine($"  Capture outcome: {outcome1}. Pending reviews: {pending.Count} (\"{name}\"). Matches for the same question: {found.Count}.");
    Console.WriteLine();

    // Act 2: a person approves, and a differently worded question now recalls it.
    Console.WriteLine($"Act 2. A person approves the Reasonlet. Then ask \"{RefundAgainQuestion}\".");
    RequireSuccess(await memory.ApproveAsync(scope, pending[0].Definition.Id, ct), "Act 2: approve");
    var firstCall = refunds.Requests.Count;
    reply = await RunAsync(refundClient, RefundAgainQuestion, ct);
    RequireEqual(reply, RefundAnswer, "Act 2: the reply");
    var outcome2 = await watcher.NextOutcomeAsync(TimeSpan.FromSeconds(30), ct);
    RequireEqual(outcome2, "execution_only", "Act 2: the capture outcome");
    var injected = refunds.SystemText(firstCall).Contains(name, StringComparison.Ordinal);
    RequireEqual(injected, true, "Act 2: whether the first call carried the recalled Reasonlet");
    var stillPending = Require(await memory.ListPendingAsync(scope, 10, null, ct), "Act 2: list pending");
    RequireEqual(stillPending.Count, 0, "Act 2: the pending reviews");
    Console.WriteLine($"  The first call to the model carried the recalled Reasonlet \"{name}\" in its system text.");
    Console.WriteLine($"  Capture outcome: {outcome2}. Pending reviews: {stillPending.Count}.");
    Console.WriteLine();

    // Act 3: a different tool shape, with a card number in the question. AI.Sentinel blocks the capture, so the store
    // never sees it. Without the guard this shape would have been stored as a new pending Reasonlet.
    Console.WriteLine($"Act 3. Ask \"{CardQuestion}\" through a client with a different tool shape (invoices_get).");
    var invoices = new ScriptedChatClient([new ScriptedToolCall("invoices_get", new Dictionary<string, object?>())], InvoiceAnswer);
    var invoiceClient = BuildClient(invoices, provider, scope);
    reply = await RunAsync(invoiceClient, CardQuestion, ct);
    RequireEqual(reply, InvoiceAnswer, "Act 3: the reply");
    var outcome3 = await watcher.NextOutcomeAsync(TimeSpan.FromSeconds(30), ct);
    RequireEqual(outcome3, "Blocked", "Act 3: the capture outcome");
    var pending3 = Require(await memory.ListPendingAsync(scope, 10, null, ct), "Act 3: list pending");
    RequireEqual(pending3.Count, 0, "Act 3: the pending reviews");
    var cardMatches = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = CardQuestion, Top = 3 }, ct), "Act 3: recall");
    RequireEqual(cardMatches.Count, 0, "Act 3: the matches for the card question");
    Console.WriteLine($"  Capture outcome: {outcome3}. Pending reviews: {pending3.Count}. Matches for the card question: {cardMatches.Count}.");
    Console.WriteLine();

    return 0;
}

// The host tools return fixed strings. The names match the scripted calls.
static IChatClient BuildClient(ScriptedChatClient scripted, IServiceProvider provider, ReasonletScope scope) =>
    new ChatClientBuilder(scripted)
        .UseReasonlets(new ReasonletChatOptions { DefaultScope = scope })
        .UseFunctionInvocation()
        .Build(provider);

static async Task<string> RunAsync(IChatClient client, string question, CancellationToken ct)
{
    var options = new ChatOptions
    {
        Tools =
        [
            AIFunctionFactory.Create(() => "order 1007, paid", "orders_get"),
            AIFunctionFactory.Create(() => "refund REF-4821 issued", "payments_refund"),
            AIFunctionFactory.Create(() => "invoice INV-1007", "invoices_get"),
        ],
    };
    var response = await client.GetResponseAsync(question, options, ct);
    return response.Text;
}

static void RequireEqual<T>(T actual, T expected, string what)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        throw new SampleFailure($"{what} was {actual}, not {expected}.");
    }
}

static T Require<T>(Result<T, MnemosyneError> result, string what) =>
    result.IsSuccess ? result.Value : throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");

static void RequireSuccess(UnitResult<MnemosyneError> result, string what)
{
    if (result.IsFailure)
    {
        throw new SampleFailure($"{what} failed: {result.Error.Code}: {result.Error.Message}");
    }
}

/// <summary>A call the sample depends on failed; the message says which.</summary>
internal sealed class SampleFailure(string message) : Exception(message);
