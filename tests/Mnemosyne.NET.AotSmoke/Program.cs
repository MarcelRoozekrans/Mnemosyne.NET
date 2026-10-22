using Mnemosyne.NET.Abstractions;

// The publish proves the packages compile clean under native AOT. This round-trip proves the source-generated
// graph serializer also works at runtime, and the hash check proves the canonical hash matches the JIT result.
// The CI job runs the binary and fails on a non-zero exit code.
const string Graph =
    """{"nodes":[{"id":"a","type":"tool","name":"Search <é>","tool":"search","properties":{"tool.args":{"q":["x",1,2.5,true,null]}}},{"id":"b","type":"answer"}],"edges":[{"from":"a","to":"b","condition":"found"}],"schemaVersion":1}""";

var read = ReasonletGraphJson.Deserialize(Graph);
if (read.IsFailure)
{
    Console.Error.WriteLine($"Deserialize failed: {read.Error.Message}");
    return 1;
}

var written = ReasonletGraphJson.Serialize(read.Value);
if (!string.Equals(written, Graph, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Round-trip differs:{Environment.NewLine}expected {Graph}{Environment.NewLine}actual   {written}");
    return 2;
}

// The SHA-256 of the canonical form of Graph; that form is a permanent contract, so this never changes.
const string ExpectedHash = "eb0d2c57c5c061d06ae9ef389db2469eea3658a4c0b28dad31918cbfac84e448";

var hash = read.Value.ComputeContentHash();
if (hash.IsFailure || !string.Equals(hash.Value, ExpectedHash, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Content hash differs:{Environment.NewLine}expected {ExpectedHash}{Environment.NewLine}actual   {(hash.IsSuccess ? hash.Value : hash.Error.Message)}");
    return 3;
}

return 0;
