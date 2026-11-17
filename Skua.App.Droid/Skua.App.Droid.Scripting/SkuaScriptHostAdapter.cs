using Skua.App.Droid.Core.Engine;
using Skua.Core.Headless;

namespace Skua.App.Droid.Scripting;

// =====================================================================================================================
//  PENDING ADAPTER - TO BE COMPLETED WHEN THE SKUA.CORE HEADLESS HOST PROJECT LANDS
//
//  This class is the single place where the Android app meets Skua.Core's script engine. It compiles and behaves
//  correctly today (it reports "not available" and refuses to start), so the rest of the app is finished and tested
//  without it. Everything marked HOST-PROJECT below is what the host project (HeadlessFlashUtil + its DI entry
//  point, e.g. AddHeadlessSkua(...), and its session facade) must supply. Do not move script logic anywhere else.
// =====================================================================================================================

/// <summary>
/// <see cref="IScriptHost"/> on Skua.Core (ScriptManager, the IScriptInterface API) through the headless host project.
/// Not functional yet: see the HOST-PROJECT notes.
/// </summary>
public sealed class SkuaScriptHostAdapter : IScriptHost
{
    private readonly ScriptReferenceSet _references;
    private readonly ScriptAssemblyStore _assemblies;
    private string? _loaded;
    private AqwSession? _session;

    /// <param name="references">Reference images from the APK (see <see cref="ScriptReferenceSet"/>).</param>
    /// <param name="assemblies">The process-wide content-addressed assembly cache.</param>
    /// <param name="scriptsDirectory">Equals Skua.Core's ClientFileSources.SkuaScriptsDIR (see AppPaths).</param>
    public SkuaScriptHostAdapter(ScriptReferenceSet references, ScriptAssemblyStore assemblies, string scriptsDirectory)
    {
        _references = references;
        _assemblies = assemblies;
        ScriptsDirectory = scriptsDirectory;

        // HOST-PROJECT: build the host here, e.g.
        //   IServiceCollection services = new ServiceCollection();
        //   services.AddHeadlessSkua(new HeadlessSkuaPlatform
        //   {
        //       ReferenceProvider = _references.Provider,               // IScriptReferenceProvider
        //       NamespaceProvider = ScriptReferenceSet.Namespaces,       // IScriptNamespaceProvider
        //       AssemblyLoader    = (image, deps) => _assemblies.GetOrLoad(image, deps),  // see contract below
        //       ScriptsDirectory  = scriptsDirectory,
        //       Dispatcher / Dialogs / Clipboard = no-op Android implementations,
        //   });
        //   _facade = services.BuildServiceProvider().GetRequiredService<IHeadlessSkua>();
        // and flip Support to available when _references.IsComplete.
        string why = references.IsComplete
            ? UnavailableScriptHost.DefaultReason
            : $"Script references are incomplete ({string.Join(", ", references.Problems.Take(3))}).";
        Support = new ScriptSupport(false, why);
    }

    public string ScriptsDirectory { get; }

    public ScriptSupport Support { get; }

    public ScriptStatus Status => new(ScriptRunState.Idle, _loaded, null, null);

    /// <summary>Distinct script/include images loaded in this process (never freed on Mono).</summary>
    public int LoadedAssemblyCount => _assemblies.Count;

    public event EventHandler<ScriptStatus>? StatusChanged;

    // HOST-PROJECT: raise for every line the script writes (bot.Log, Trace output, compile errors), Source = "script".
    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
        // HOST-PROJECT: the facade's HeadlessFlashUtil must read the world model and send packets through THIS session
        // (the engine replaces it after each logout, and calls Attach again with the new one).
        _session = session;
    }

    public void Load(string scriptPath)
    {
        // HOST-PROJECT: ScriptManager.SetLoadedScript(scriptPath).
        _loaded = scriptPath;
        StatusChanged?.Invoke(this, Status);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // HOST-PROJECT: facade.StartScriptAsync(): ScriptManager.StartScript() on its own thread (16 MB stack, like
        // the spike), mapping Compiling/Running/Faulted/Idle to StatusChanged. Assembly loading MUST go through
        // _assemblies.GetOrLoad (never a new collectible ScriptLoadContext per run), see ScriptAssemblyStore.
        throw new EngineException(Support.Reason ?? UnavailableScriptHost.DefaultReason);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // HOST-PROJECT: facade.StopScriptAsync(): ScriptManager.StopScript(), awaited with cancellationToken (the
        // engine bounds it with a timeout).
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _session = null;
        return ValueTask.CompletedTask;
    }
}
