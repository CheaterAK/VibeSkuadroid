using Skua.Core.Headless;

namespace Skua.App.Droid.Core.Engine;

/// <summary>
/// The script half of <see cref="HeadlessBotEngine"/>: compiles and runs Skua scripts against the session the
/// engine owns. Implemented by the Skua.Core host adapter (Skua.App.Droid.Scripting/SkuaScriptHostAdapter.cs, an
/// <see cref="ISessionScriptHost"/>); <see cref="UnavailableScriptHost"/> stands in where scripts cannot run.
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

    /// <summary>
    /// Starts the loaded script with <paramref name="options"/> (an option profile, unattended). Hosts without
    /// option profiles accept only the defaults.
    /// </summary>
    Task StartAsync(ScriptStartOptions options, CancellationToken cancellationToken) =>
        options.OptionStorage is null
            ? StartAsync(cancellationToken)
            : throw new EngineException("Option profiles are not available in this engine.");

    /// <summary>Whether a running script can be paused and resumed.</summary>
    bool CanPause => false;

    /// <summary>
    /// Pauses or resumes the running script (Skua's pause: the script thread waits in place at its next Sleep or
    /// Wait). Completes when the change is applied; <see cref="StatusChanged"/> reports <see cref="ScriptStatus.Paused"/>.
    /// </summary>
    /// <returns>False when the change was superseded (the script stopped, or another pause or resume came first).</returns>
    /// <exception cref="EngineException">No script is running, or this host cannot pause.</exception>
    Task<bool> SetPausedAsync(bool paused, CancellationToken cancellationToken) =>
        throw new EngineException("Pausing scripts is not available in this engine.");
}

/// <summary>
/// An <see cref="IScriptHost"/> that owns the game session: the Skua.Core host, whose login, relogin and Flash bridge
/// all run on one <see cref="AqwSession"/> created once and kept for the engine's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// With such a host the engine never creates or replaces sessions itself. It calls <see cref="OpenSession"/> once
/// (instead of its session factory and <see cref="IScriptHost.Attach"/>), logs in and reconnects through the host
/// (Skua's own login code), and on logout asks the host to drop every credential in place
/// (<see cref="LogoutAsync"/>), so the engine, the host's Flash bridge and Skua's services always share one session.
/// </para>
/// <para>
/// Reconnect ownership: while <see cref="OwnsReconnect"/> is true (a script is compiling, running or stopping, or
/// the host's own relogin is pending or running) the host reconnects by itself and the engine's reconnect loop waits.
/// Otherwise the engine reconnects, through <see cref="ReconnectAsync"/>.
/// </para>
/// </remarks>
public interface ISessionScriptHost : IScriptHost
{
    /// <summary>Creates the host and its session. Called once by the engine; null when the host could not start (the engine then runs without it).</summary>
    AqwSession? OpenSession();

    /// <summary>The app's auto-reconnect setting; applied as the script's relogin option each time a script starts.</summary>
    bool AutoRelogin { get; set; }

    /// <summary>The host is reconnecting by itself (or a script is busy and will): the engine must not.</summary>
    bool OwnsReconnect { get; }

    /// <summary>Counters of the current or last run (null before any login).</summary>
    ScriptCounters? Counters { get; }

    /// <summary>Logs in and joins a server through the host's login code. Returns the server joined.</summary>
    /// <param name="request">The login.</param>
    /// <param name="connecting">Called with the server name when the web login is done and joining starts.</param>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <exception cref="EngineException">The login or the join failed (user-presentable message).</exception>
    Task<string> LoginAsync(LoginRequest request, Action<string> connecting, CancellationToken cancellationToken);

    /// <summary>One reconnect attempt with the credentials of the last login.</summary>
    /// <exception cref="EngineException">The attempt failed.</exception>
    Task ReconnectAsync(string? server, CancellationToken cancellationToken);

    /// <summary>Stops the script, logs out (intentionally: no relogin) and forgets every credential and token.</summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    /// <summary>Compiles the script (or reuses its compile) and returns its options with their saved values.</summary>
    Task<ScriptOptionSet> GetOptionsAsync(string scriptPath, CancellationToken cancellationToken);

    /// <summary>Saves edited option values where the script reads them (Skua's options file for the script's storage).</summary>
    Task SaveOptionsAsync(ScriptOptionSet options, CancellationToken cancellationToken);

    /// <summary>
    /// The script's options read from <paramref name="optionStorage"/> instead of the storage the script names (an
    /// option profile, e.g. a queue entry's); null reads the script's own. Saving the returned set writes that storage.
    /// </summary>
    Task<ScriptOptionSet> GetOptionsAsync(string scriptPath, string? optionStorage, CancellationToken cancellationToken) =>
        optionStorage is null
            ? GetOptionsAsync(scriptPath, cancellationToken)
            : throw new EngineException("Option profiles are not available in this engine.");
}

/// <summary>What a script run did so far (Skua's bot stats).</summary>
public sealed record ScriptCounters(int Kills, int Deaths, int QuestsCompleted, int Drops, int Relogins)
{
    public string Display => $"kills {Kills} · deaths {Deaths} · quests {QuestsCompleted} · drops {Drops}{(Relogins > 0 ? $" · relogins {Relogins}" : string.Empty)}";
}

/// <summary>How an option is edited.</summary>
public enum ScriptOptionKind
{
    Toggle,
    Number,
    Text,
    Choice,
}

/// <summary>One script option.</summary>
/// <param name="Category">"Options" or the name of the script's option group.</param>
/// <param name="Name">The option's key.</param>
/// <param name="DisplayName">What the desktop options window shows.</param>
/// <param name="Description">Help text.</param>
/// <param name="Kind">How it is edited.</param>
/// <param name="Value">Current value as text (as Skua stores it).</param>
/// <param name="Choices">The allowed values of a <see cref="ScriptOptionKind.Choice"/>.</param>
/// <param name="Transient">Not saved: reset to its default every run.</param>
public sealed record ScriptOptionItem(string Category, string Name, string DisplayName, string Description, ScriptOptionKind Kind, string Value, IReadOnlyList<string> Choices, bool Transient)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>A script's options as its options window would show them.</summary>
/// <param name="ScriptPath">The script.</param>
/// <param name="Storage">Skua's option storage name (the options file).</param>
/// <param name="Items">The options, in the script's order.</param>
public sealed record ScriptOptionSet(string ScriptPath, string Storage, IReadOnlyList<ScriptOptionItem> Items);

/// <summary>The script host used when no Skua.Core host is available: reports why and refuses to start.</summary>
public sealed class UnavailableScriptHost : IScriptHost
{
    public const string DefaultReason = "Scripts cannot run in this engine (no script host).";

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
