using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core;
using Skua.Core.Interfaces;
using Skua.Core.PortabilityTests;
using Skua.Core.Scripts;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Usage: dotnet run --project Skua.Core.PortabilityTests [-- --compile-file <script.cs> ...]
// The optional files (for example a community CoreBots.cs) are compiled, not run, through the stream seam.

// Keep the compiled-script disk cache out of the real profile. Must run before any Skua type is touched,
// because ClientFileSources captures the ApplicationData folder (XDG_CONFIG_HOME on Linux) once.
string sandbox = Path.Combine(Path.GetTempPath(), $"skua-portability-{Environment.ProcessId}");
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);

List<string> extraFiles = new();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--compile-file" && i + 1 < args.Length)
        extraFiles.Add(args[++i]);
}

Checks checks = new();

// Images only, no file locations: the same shape the Android host gets from APK assets.
StreamScriptReferenceProvider streamProvider = StreamScriptReferenceProvider.FromImages(CollectReferenceImages());
IServiceProvider seamServices = new ServiceCollection()
    .AddSingleton<IScriptReferenceProvider>(streamProvider)
    .AddSingleton<IScriptNamespaceProvider>(ScriptNamespaceProvider.Desktop)
    .BuildServiceProvider();

checks.Run("stream provider yields in-memory references", () =>
{
    IReadOnlyList<PortableExecutableReference> refs = streamProvider.GetReferences();
    Checks.Assert(refs.Count > 50, $"expected the framework and Skua references, got {refs.Count}");
    Checks.Assert(refs.All(r => r.FilePath is not null && !r.FilePath.Contains(Path.DirectorySeparatorChar)),
        "references should carry only their names, not file paths");
    Checks.Assert(refs.Any(r => r.FilePath == "Skua.Core.dll"), "Skua.Core.dll missing from references");
    return $"{refs.Count} references";
});

checks.Run("compile, load and run a script through the seam", () =>
{
    Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(seamServices);
    ScriptLoadContext loadContext = new();
    object script = CompileOrThrow(compiler, Scripts.Basic, "PortabilityBasic", loadContext);

    Checks.Assert(AssemblyLoadContext.GetLoadContext(script.GetType().Assembly) == loadContext,
        "script assembly was not loaded into the ScriptLoadContext");

    ScriptInterfaceStub stub = RunScriptMain(script);
    Checks.Assert(stub.Logs.Contains("sum=6"), $"unexpected logs: {string.Join(" | ", stub.Logs)}");
    Checks.Assert(stub.Logs.Contains("json=True"), $"unexpected logs: {string.Join(" | ", stub.Logs)}");
    return string.Join(" | ", stub.Logs);
});

checks.Run("WinForms-style script compiles and runs against the shim", () =>
{
    Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(seamServices);
    object script = CompileOrThrow(compiler, Scripts.WinForms, "PortabilityWinForms", new ScriptLoadContext());

    ScriptInterfaceStub stub = RunScriptMain(script);
    Checks.Assert(stub.Logs.Contains("answer=No"), $"unexpected logs: {string.Join(" | ", stub.Logs)}");
    Checks.Assert(stub.Logs.Contains("invoked=True"), $"unexpected logs: {string.Join(" | ", stub.Logs)}");
    return string.Join(" | ", stub.Logs);
});

checks.Run("default desktop provider still works when nothing is registered", () =>
{
    Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(new ServiceCollection().BuildServiceProvider());
    object script = CompileOrThrow(compiler, Scripts.Basic.Replace("PortabilityScript", "PortabilityDesktopScript"), "PortabilityDesktop", loadContext: null);
    ScriptInterfaceStub stub = RunScriptMain(script);
    Checks.Assert(stub.Logs.Contains("sum=6"), $"unexpected logs: {string.Join(" | ", stub.Logs)}");
    return $"{DesktopScriptReferenceProvider.Shared.GetReferences().Count} references";
});

checks.Run("script load context unloads after the script is released", () =>
{
    WeakReference contextRef = CompileRunAndRelease(seamServices);
    Compiler.ClearSessionRegistries();
    for (int i = 0; i < 10 && contextRef.IsAlive; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    Checks.Assert(!contextRef.IsAlive, "ScriptLoadContext was not collected");
    return "collected";
});

foreach (string file in extraFiles)
{
    checks.Run($"compile {Path.GetFileName(file)}", () =>
    {
        Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(seamServices);
        CompileOrThrow(compiler, File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), new ScriptLoadContext(), instantiate: false);
        return "compiled";
    });
}

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch
{
}

return checks.Report();

static IEnumerable<KeyValuePair<string, byte[]>> CollectReferenceImages()
{
    // On Android these would be read from assets; here they are read from disk into memory,
    // so the compiler never sees a file path.
    string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    IEnumerable<string> files = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
        .Concat(Directory.EnumerateFiles(runtimeDir, "*.dll"));

    foreach (string file in files)
        yield return new(Path.GetFileName(file), File.ReadAllBytes(file));
}

static object CompileOrThrow(Compiler compiler, string source, string scriptName, ScriptLoadContext? loadContext, bool instantiate = true)
{
    int hash = HashCode.Combine(source, scriptName, Guid.NewGuid());
    if (!instantiate)
    {
        Type? type = compiler.CompileClassToType(source, hash, loadContext, scriptName);
        if (compiler.Error || type is null)
            throw new InvalidOperationException($"Compilation failed:\n{compiler.ErrorMessage}");
        return type;
    }

    object? instance = compiler.CompileClass(source, hash, loadContext, scriptName);
    if (compiler.Error || instance is null)
        throw new InvalidOperationException($"Compilation failed:\n{compiler.ErrorMessage}");
    return instance;
}

static ScriptInterfaceStub RunScriptMain(object script)
{
    IScriptInterface bot = DispatchProxy.Create<IScriptInterface, ScriptInterfaceStub>();
    MethodInfo scriptMain = script.GetType().GetMethod("ScriptMain")
        ?? throw new InvalidOperationException("ScriptMain not found");
    scriptMain.Invoke(script, new object[] { bot });
    return (ScriptInterfaceStub)(object)bot;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CompileRunAndRelease(IServiceProvider services)
{
    Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(services);
    ScriptLoadContext loadContext = new();
    object script = CompileOrThrow(compiler, Scripts.Basic.Replace("PortabilityScript", "PortabilityUnloadScript"), "PortabilityUnload", loadContext);
    RunScriptMain(script);
    loadContext.Unload();
    return new WeakReference(loadContext);
}
