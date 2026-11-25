using System.Diagnostics;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Models;
using Compiler = Skua.Core.Compiler;

namespace Skua.App.Droid.Tests;

/// <summary>
/// The compiler process off Android: <c>Skua.App.Droid.CompilerHost</c> run as a real child process per request
/// (stdin/stdout instead of a Messenger), exactly the worker the Android <c>:compiler</c> service runs.
/// </summary>
internal sealed class ProcessCompileService : IScriptCompileService
{
    private readonly string _data;
    private readonly string _refs;
    private readonly string? _lockFile;

    public ProcessCompileService(string data, string refs, string? lockFile = null)
    {
        _data = data;
        _refs = refs;
        _lockFile = lockFile;
    }

    public static string RefsDir => Path.Combine(AppContext.BaseDirectory, "script-refs");

    /// <summary>The child dies on purpose (Environment.FailFast) before compiling: a compiler Android killed.</summary>
    public bool Die { get; set; }

    public List<CompileResponse> Responses { get; } = new();

    public async Task<CompileResponse> CompileAsync(CompileRequest request, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Skua.App.Droid.CompilerHost.dll"));
        psi.ArgumentList.Add(_data);
        psi.ArgumentList.Add(_refs);
        if (_lockFile is not null)
            psi.ArgumentList.Add(_lockFile);
        if (Die)
            psi.Environment["SKUA_COMPILER_HOST_DIE"] = "1";
        using Process child = Process.Start(psi) ?? throw new CompileServiceException("the compiler host did not start");
        try
        {
            await child.StandardInput.BaseStream.WriteAsync(ScriptCompileProtocol.Encode(request), cancellationToken);
            child.StandardInput.Close();
            using MemoryStream output = new();
            Task copy = child.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            Task<string> errors = child.StandardError.ReadToEndAsync(cancellationToken);
            await child.WaitForExitAsync(cancellationToken);
            await copy;
            CompileResponse? response = ScriptCompileProtocol.DecodeResponse(output.ToArray());
            if (child.ExitCode != 0 || response is null)
                throw new CompileServiceException($"the compiler host ended with {child.ExitCode} without an answer: {(await errors).Split('\n').FirstOrDefault()}", died: true);
            lock (Responses)
                Responses.Add(response);
            return response;
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }
}

public class CompileProtocolTests
{
    [Fact]
    public void RequestsAndResponsesRoundTripAndAnythingElseIsRejected()
    {
        CompileRequest request = new("abc", "/data/Scripts/Farm/X.cs", "refs1");
        Assert.Equal(request, ScriptCompileProtocol.DecodeRequest(ScriptCompileProtocol.Encode(request)));
        CompileResponse response = new("abc", CompileOutcome.CompileError, "CS1002: ; expected", "1: class X {", 1.5, 300L << 20, 2);
        Assert.Equal(response, ScriptCompileProtocol.DecodeResponse(ScriptCompileProtocol.Encode(response)));
        Assert.Contains("\"outcome\":\"CompileError\"", System.Text.Encoding.UTF8.GetString(ScriptCompileProtocol.Encode(response)));

        Assert.Null(ScriptCompileProtocol.DecodeRequest(null));
        Assert.Null(ScriptCompileProtocol.DecodeRequest("not json"u8.ToArray()));
        Assert.Null(ScriptCompileProtocol.DecodeRequest("{\"v\":2,\"id\":\"a\",\"scriptPath\":\"/x.cs\",\"referenceSet\":\"r\"}"u8.ToArray()));
        Assert.Null(ScriptCompileProtocol.DecodeRequest("{\"v\":1,\"id\":\"\",\"scriptPath\":\"/x.cs\",\"referenceSet\":\"r\"}"u8.ToArray()));
        Assert.Null(ScriptCompileProtocol.DecodeResponse(new byte[ScriptCompileProtocol.MaxMessageBytes + 1]));

        // A huge diagnostic is cut so the answer still fits one Binder message.
        CompileResponse huge = response with { Message = new string('e', 400_000), Code = new string('c', 400_000) };
        byte[] encoded = ScriptCompileProtocol.Encode(huge);
        Assert.True(encoded.Length <= ScriptCompileProtocol.MaxMessageBytes);
        Assert.EndsWith("(cut)", ScriptCompileProtocol.DecodeResponse(encoded)!.Message);
    }

    [Fact]
    public void InProcessFallbackNeedsRoomForACompile()
    {
        Assert.Null(ScriptPrecompiler.RefuseInProcessFor(DeviceMemory.Unknown));
        Assert.Null(ScriptPrecompiler.RefuseInProcessFor(new DeviceMemory(10L << 30, 3L << 30, 300L << 20)));
        string? refusal = ScriptPrecompiler.RefuseInProcessFor(new DeviceMemory(10L << 30, 900L << 20, 300L << 20));
        Assert.NotNull(refusal);
        Assert.Contains("900 MB", refusal);
    }
}

public partial class SessionIntegrationTests
{
    private const string CompiledInclude = """
        public class DroidCompiledInclude
        {
            public static string Hello() => "hello from the compiler process";
        }
        """;

    private const string CompiledScript = """
        //cs_include Scripts/DroidCompiler/DroidCompiledInclude.cs
        using Skua.Core.Interfaces;

        public class DroidCompiledRun
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log(DroidCompiledInclude.Hello() + " ran");
            }
        }
        """;

    /// <summary>
    /// The engine with the compiler process: a script and its include are compiled by the child process into the cache
    /// under the keys Skua.Core uses, and the engine process loads them without one Roslyn compilation of its own; a
    /// compile error comes back as Skua's; a compiler that dies is reported and, when memory allows, the engine compiles
    /// itself, else it refuses with a clear message; nothing hangs.
    /// </summary>
    [Fact]
    public async Task TheCompilerProcessCompilesAndTheEngineOnlyLoads()
    {
        await using FakeAqw aqw = new();
        ScriptReferenceSet refs = ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(ProcessCompileService.RefsDir));
        SkuaScriptHostAdapter host = new(refs, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
            o.Game = new HeadlessGameOptions { Session = aqw.NewSession().Options, Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(50) }, DownloadMapCells = false };
        });
        // The child must see the same Skua data folder (so the same scripts folder, cache and keys) as this process.
        string data = Path.GetDirectoryName(ClientFileSources.SkuaDIR)!;
        ProcessCompileService service = new(data, ProcessCompileService.RefsDir, Path.Combine(Path.GetTempPath(), $"skua-compile-{Environment.ProcessId}.lock"));
        string? refusal = null;
        host.Precompiler = new ScriptPrecompiler(service, refs.Identity, () => refusal) { Timeout = TimeSpan.FromMinutes(3) };
        List<LogEntry> log = new();
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        engine.LogAdded += (_, e) =>
        {
            lock (log)
                log.Add(e);
        };
        bool Logged(string text)
        {
            lock (log)
                return log.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
        }
        try
        {
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null);
            string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidCompiler");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "DroidCompiledInclude.cs"), CompiledInclude);
            string script = Path.Combine(folder, "DroidCompiledRun.cs");
            await File.WriteAllTextAsync(script, CompiledScript + "\n// " + Guid.NewGuid()); // a fresh cache key

            // Cold: the child compiles the include and the script; the engine compiles nothing.
            long before = Compiler.Compilations;
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => Logged("hello from the compiler process ran"), 60000);
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
            Assert.Equal(before, Compiler.Compilations);
            Assert.Equal(0, host.InProcessCompilations);
            Assert.Equal(0, refs.Provider.Loads); // the engine read no reference image either
            CompileResponse cold = Assert.Single(service.Responses);
            Assert.Equal(CompileOutcome.Compiled, cold.Outcome);
            Assert.True(cold.Compilations >= 1, $"{cold.Compilations} compilations");
            Assert.True(Logged("by the compiler process"), Dump(log));
            Assert.Empty(Directory.GetFiles(Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts"), "*.partial"));

            // Warm: the child finds everything cached (same keys) and compiles nothing either.
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle && service.Responses.Count == 2, 30000);
            Assert.Equal(0, service.Responses[1].Compilations);
            Assert.Equal(before, Compiler.Compilations);

            // A compile error: Skua's message, the script does not start, nothing is compiled here.
            string broken = Path.Combine(folder, "DroidBroken.cs");
            await File.WriteAllTextAsync(broken, "using Skua.Core.Interfaces;\npublic class DroidBroken { public void ScriptMain(IScriptInterface bot) { int x = ; } }");
            EngineException error = await Assert.ThrowsAsync<EngineException>(() => engine.StartScriptAsync(broken));
            Assert.StartsWith("Compile error:", error.Message);
            Assert.Equal(ScriptRunState.Faulted, engine.Snapshot.Script.State);
            Assert.Equal(before, Compiler.Compilations);

            // The compiler dies and memory is short: a clear refusal, no in-process compile, no hang.
            await File.WriteAllTextAsync(script, CompiledScript + "\n// " + Guid.NewGuid());
            service.Die = true;
            refusal = "only 600 MB of memory is available";
            EngineException refused = await Assert.ThrowsAsync<EngineException>(() => engine.StartScriptAsync(script));
            Assert.Contains("ended while compiling", refused.Message);
            Assert.Contains("only 600 MB", refused.Message);
            Assert.Equal(ScriptRunState.Faulted, engine.Snapshot.Script.State);
            Assert.Equal(before, Compiler.Compilations);

            // The compiler dies and memory allows: the engine compiles itself, and says so.
            refusal = null;
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 60000);
            Assert.True(Logged("compiling in the app's own process instead"), Dump(log));
            Assert.True(host.InProcessCompilations >= 1);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>Two engines compiling at once (army slots): their compiler processes take turns on the app-wide lock, both succeed.</summary>
    [Fact]
    public async Task CompilersOfSeveralEnginesTakeTurns()
    {
        ScriptReferenceSet refs = ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(ProcessCompileService.RefsDir));
        string lockFile = Path.Combine(Path.GetTempPath(), $"skua-compile-turns-{Environment.ProcessId}.lock");
        string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidCompiler");
        Directory.CreateDirectory(folder);
        string[] scripts = new string[2];
        for (int i = 0; i < scripts.Length; i++)
        {
            scripts[i] = Path.Combine(folder, $"Turn{i}.cs");
            await File.WriteAllTextAsync(scripts[i], $"public class Turn{i} {{ public string Id = \"{Guid.NewGuid()}\"; }}");
        }
        string data = Path.GetDirectoryName(ClientFileSources.SkuaDIR)!;
        CompileResponse[] answers = await Task.WhenAll(scripts.Select((s, i) =>
            new ProcessCompileService(data, ProcessCompileService.RefsDir, lockFile).CompileAsync(new CompileRequest($"t{i}", s, refs.Identity), CancellationToken.None)));
        Assert.All(answers, a => Assert.Equal(CompileOutcome.Compiled, a.Outcome));
        Assert.All(answers, a => Assert.Equal(1, a.Compilations));
    }

    /// <summary>Requests a compiler must refuse: another reference set, a path outside the scripts folder.</summary>
    [Fact]
    public async Task TheCompilerRefusesForeignReferencesAndPaths()
    {
        ScriptReferenceSet refs = ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(ProcessCompileService.RefsDir));
        ProcessCompileService service = new(Path.GetDirectoryName(ClientFileSources.SkuaDIR)!, ProcessCompileService.RefsDir);
        string inside = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidCompiler", "Refused.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllTextAsync(inside, "public class Refused { }");
        string outside = Path.Combine(Path.GetTempPath(), $"skua-outside-{Environment.ProcessId}.cs");
        await File.WriteAllTextAsync(outside, "public class Outside { }");
        try
        {
            CompileResponse foreign = await service.CompileAsync(new CompileRequest("1", inside, "another-app-version"), CancellationToken.None);
            Assert.Equal(CompileOutcome.Refused, foreign.Outcome);
            Assert.Contains("references differ", foreign.Message);
            CompileResponse escaped = await service.CompileAsync(new CompileRequest("2", outside, refs.Identity), CancellationToken.None);
            Assert.Equal(CompileOutcome.Refused, escaped.Outcome);
            CompileResponse dotted = await service.CompileAsync(new CompileRequest("3", Path.Combine(ClientFileSources.SkuaScriptsDIR, "..", "x.cs"), refs.Identity), CancellationToken.None);
            Assert.Equal(CompileOutcome.Refused, dotted.Outcome);
            Assert.All(new[] { foreign, escaped, dotted }, r => Assert.Equal(0, r.Compilations));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
