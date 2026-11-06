using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.AgentFramework;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.Samples;
using Npgsql;
using ZeroAlloc.Results;

// Runs an Agent Framework agent offline and shows what Mnemosyne does with each run. A scripted client stands in for
// the model, so the sample needs no API key. The agent is a ChatClientAgent with a ReasonletContextProvider: before a
// run the provider recalls the best known procedure and adds it to the run's instructions, and after a run it hands the
// run to the background capture.
// The sample checks its own receipts and exits 1 if they differ, so it fails when what the docs quote about the agent
// provider no longer holds.
//
// Needs PostgreSQL 15 or later with pgvector 0.8.0 or later: `docker compose up -d --wait` next to this file starts
// one. MNEMOSYNE_SAMPLE_PG overrides the connection string. The sample migrates the mnemosyne schema with 256
// dimensions, so point it at a database of its own.
const string DefaultConnectionString = "Host=localhost;Port=5435;Username=postgres;Password=postgres;Database=mnemosyne_agent_sample";
const string RefundQuestion = "How do I refund an order?";
const string RefundAgainQuestion = "How do I refund an order for a customer?";
const string RefundAnswer = "Refund REF-4821 was issued and the customer was notified by email.";

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
    // 1. The host wiring: a data source, the PostgreSQL store, an embedding generator, the capture queue and the facade.
    // The review gate is off, so a captured procedure is recallable at once.
    var services = new ServiceCollection();
    services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
    services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
    services.AddMnemosyne();
    services.AddReasonletCapture();
    await using var provider = services.BuildServiceProvider();
    Console.WriteLine($"Wired the PostgreSQL store, the hashing embedding generator ({HashingEmbeddingGenerator.Dimensions} dimensions) and the Mnemosyne facade.");

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
        // 3. Erase the scope, whatever happened above, so a run leaves no Reasonlets behind. A failed erase is
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

    var scripted = new ScriptedChatClient(
        [new ScriptedToolCall("orders_get", new Dictionary<string, object?>()), new ScriptedToolCall("payments_refund", new Dictionary<string, object?>())],
        RefundAnswer);
    var agent = BuildAgent(scripted, provider, scope);

    Console.WriteLine();
    Console.WriteLine("The agent is a ChatClientAgent with a ReasonletContextProvider. A scripted client plays the model: it calls orders_get and payments_refund, then answers.");
    Console.WriteLine();

    // Run 1: a new procedure. Nothing is known yet, so the first call carries no recalled Reasonlet.
    Console.WriteLine($"Run 1. Ask \"{RefundQuestion}\" and let the capture run in the background.");
    var reply = (await agent.RunAsync(RefundQuestion, await agent.CreateSessionAsync(ct), cancellationToken: ct)).Text;
    RequireEqual(reply, RefundAnswer, "Run 1: the reply");
    var outcome1 = await NextOutcomeAsync(watcher, "Run 1", ct);
    RequireEqual(outcome1, "new_reasonlet", "Run 1: the capture outcome");
    var found = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = RefundQuestion, Top = 3 }, ct), "Run 1: recall");
    RequireEqual(found.Count, 1, "Run 1: the matches for the same question");
    var name = found[0].Reasonlet.Name;
    RequireEqual(found[0].Evidence.Runs, 1, "Run 1: the runs of the Reasonlet");
    // The name exists only now, but SystemText(0) was recorded when the first call was made, so it still shows what that call carried.
    RequireEqual(scripted.SystemText(0).Contains(name, StringComparison.Ordinal), false, "Run 1: whether the first call carried a recalled Reasonlet");
    Console.WriteLine($"  Capture outcome: {outcome1}. Matches for the same question: {found.Count} (\"{name}\", {found[0].Evidence.Runs} run).");
    Console.WriteLine();

    // Run 2: a differently worded question on a fresh session. The provider recalls the Reasonlet and adds it to the
    // run's instructions. It must arrive there and never as a message of the conversation.
    Console.WriteLine($"Run 2. Ask \"{RefundAgainQuestion}\" on a fresh session.");
    var firstCall = scripted.Requests.Count;
    reply = (await agent.RunAsync(RefundAgainQuestion, await agent.CreateSessionAsync(ct), cancellationToken: ct)).Text;
    RequireEqual(reply, RefundAnswer, "Run 2: the reply");
    var outcome2 = await NextOutcomeAsync(watcher, "Run 2", ct);
    RequireEqual(outcome2, "execution_only", "Run 2: the capture outcome");
    var instructions = scripted.Options[firstCall]?.Instructions ?? string.Empty;
    RequireEqual(instructions.Contains(name, StringComparison.Ordinal), true, "Run 2: whether the first call carried the recalled Reasonlet in its instructions");
    var asMessage = scripted.Requests
        .Skip(firstCall)
        .SelectMany(request => request)
        .Any(message => message.Text.Contains(name, StringComparison.Ordinal));
    RequireEqual(asMessage, false, "Run 2: whether a message of any role carried the Reasonlet");
    var again = Require(await memory.RecallAsync(new ReasonletQuery { Scope = scope, Question = RefundAgainQuestion, Top = 3 }, ct), "Run 2: recall");
    RequireEqual(again.Count, 1, "Run 2: the matches for the second question");
    RequireEqual(again[0].Reasonlet.Name, name, "Run 2: the name of the match");
    RequireEqual(again[0].Evidence.Runs, 2, "Run 2: the runs of the Reasonlet");
    Console.WriteLine($"  The first call to the model carried the recalled Reasonlet \"{name}\" in its instructions, and no message did.");
    Console.WriteLine($"  Capture outcome: {outcome2}. Runs of the Reasonlet: {again[0].Evidence.Runs}.");
    Console.WriteLine();

    return 0;
}

// Waits for the capture of a run, and names the run when none finishes in time.
static async Task<string> NextOutcomeAsync(CaptureRunWatcher watcher, string run, CancellationToken ct)
{
    try
    {
        return await watcher.NextOutcomeAsync(TimeSpan.FromSeconds(30), ct);
    }
    catch (TimeoutException ex)
    {
        throw new SampleFailure($"{run}: {ex.Message}");
    }
}

// The agent has the two host tools, with names that match the scripted calls. The scope resolver returns a fixed scope,
// so the sample can erase it; the default resolver would use the agent's name.
static ChatClientAgent BuildAgent(ScriptedChatClient scripted, IServiceProvider provider, ReasonletScope scope) =>
    new(scripted, new ChatClientAgentOptions
    {
        Name = "support-agent",
        ChatOptions = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "order 1007, paid", "orders_get"),
                AIFunctionFactory.Create(() => "refund REF-4821 issued", "payments_refund"),
            ],
        },
        AIContextProviders = [new ReasonletContextProvider(provider, new ReasonletAgentOptions { ScopeResolver = (_, _) => new ReasonletAgentScope(scope) })],
    });

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
