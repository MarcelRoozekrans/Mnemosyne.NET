using Mnemosyne.NET.Abstractions;

// The publish proves the packages compile clean under native AOT. This round-trip proves the source-generated
// graph serializer also works at runtime: the CI job runs the binary and fails on a non-zero exit code.
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

return 0;
