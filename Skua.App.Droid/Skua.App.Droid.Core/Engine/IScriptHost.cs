using Skua.Core.Headless;

namespace Skua.App.Droid.Core.Engine;

/// <summary>
/// The script half of <see cref="HeadlessBotEngine"/>: compiles and runs Skua scripts against the session the
/// engine owns. Implemented by the Skua.Core host adapter (see Skua.App.Droid.Scripting/SkuaScriptHostAdapter.cs);
/// until that lands, <see cref="UnavailableScriptHost"/> stands in.
/// </summary>
/// <remarks>
/// Contract the implementation must keep (the Android app relies on it):
/// <list type="bullet">
/// <item>Never reload an assembly whose content it already loaded in this process (Mono/Android never collects
/// AssemblyLoadContexts, so every reload leaks its full size until the process dies).</item>
/// <item>Report <see cref="LoadedAssemblyCount"/> honestly so the app can show it and decide on a restart.</item>
/// <item>Raise <see cref="StatusChanged"/> for every state change and <see cref="Log"/> for script output.</item>
/// <item>Return from <see cref="StopAsync"/> within a bounded time even if the script ignores cancellation.</item>
/// </list>
/// </remarks>
public interface IScriptHost : IAsyncDisposable
{
    /// <summary>Whether scripts can run, and why not.</summary>
    ScriptSupport Support { get; }

    /// <summary>The current state.</summary>
    ScriptStatus Status { get; }

    /// <summary>Distinct script/include assemblies loaded into this process so far (never unloaded on Android).</summary>
    int LoadedAssemblyCount { get; }

    event EventHandler<ScriptStatus>? StatusChanged;

    event EventHandler<LogEntry>? Log;

    /// <summary>
    /// Called once, right after the engine created its session and before any login, so the host can wire its
    /// IFlashUtil (and everything else Skua.Core needs) to the same session and world model.
    /// </summary>
    void Attach(AqwSession session);

    /// <summary>Remembers the script to run next.</summary>
    void Load(string scriptPath);

    /// <summary>Compiles (or reuses a cached compile of) the loaded script and starts it.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the running script; bounded wait.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>The script host used when no Skua.Core host is available: reports why and refuses to start.</summary>
public sealed class UnavailableScriptHost : IScriptHost
{
    public const string DefaultReason =
        "Script execution is not wired in this build yet: it waits for the Skua.Core headless host (HeadlessFlashUtil). "
        + "Login, map joins, live state and raw packets work.";

    private string? _loaded;

    public UnavailableScriptHost(string? reason = null)
    {
        Support = new ScriptSupport(false, reason ?? DefaultReason);
    }

    public ScriptSupport Support { get; }

    public ScriptStatus Status => new(ScriptRunState.Idle, _loaded, null, null);

    public int LoadedAssemblyCount => 0;

    public event EventHandler<ScriptStatus>? StatusChanged;

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
    }

    public void Load(string scriptPath)
    {
        _loaded = scriptPath;
        StatusChanged?.Invoke(this, Status);
    }

    public Task StartAsync(CancellationToken cancellationToken) => throw new EngineException(Support.Reason!);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
