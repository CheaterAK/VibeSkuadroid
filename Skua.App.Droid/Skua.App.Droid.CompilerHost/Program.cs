using Skua.App.Droid.Scripting;

// One compile, as the Android :compiler service does it: request (ScriptCompileProtocol JSON) on stdin, response on
// stdout, then exit (all memory back to the system).
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Skua.App.Droid.CompilerHost <data folder> <script-refs folder> [lock file] < request.json");
    return 2;
}
string data = args[0];
ScriptReferenceSet refs = ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(args[1]));
using MemoryStream input = new();
Console.OpenStandardInput().CopyTo(input);
CompileRequest? request = ScriptCompileProtocol.DecodeRequest(input.ToArray());
if (request is null)
{
    Console.Error.WriteLine("not a compile request");
    return 3;
}
// Test hook: a compiler that dies mid-compile (as when Android kills it for memory).
if (Environment.GetEnvironmentVariable("SKUA_COMPILER_HOST_DIE") == "1")
    Environment.FailFast("compiler host: dying on purpose (test)");
await using ScriptCompileWorker worker = new(refs, args.Length > 2 ? args[2] : null, o => o.DataDirectory = data);
CompileResponse response = worker.Compile(request);
using Stream output = Console.OpenStandardOutput();
output.Write(ScriptCompileProtocol.Encode(response));
return 0;
