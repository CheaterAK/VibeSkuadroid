namespace Skua.App.Droid.Core.Engine;

/// <summary>
/// Everything the app (UI, foreground service, notification) needs from the bot, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process, created by the host (on Android the foreground service owns it, so the socket and
/// the script thread outlive any activity). The UI never touches Skua.Core.Headless or Skua.Core directly; it
/// only sees this interface and the plain records in <c>EngineModels.cs</c> (and <c>IScriptHost.cs</c>).
/// </para>
/// <para>
/// Threading: every member may be called from any thread. Events are raised on background threads; subscribers
/// marshal to their UI thread themselves. <see cref="SnapshotChanged"/> is coalesced (a few per second at most).
/// </para>
/// <para>
/// Secrets: nothing this interface emits (snapshots, log lines, packets) contains the account password or the
/// socket login token.
/// </para>
/// </remarks>
public interface IBotEngine : IAsyncDisposable
{
    /// <summary>The latest state. Immutable; cheap to read.</summary>
    EngineSnapshot Snapshot { get; }

    /// <summary>Raised when <see cref="Snapshot"/> changed (coalesced; background thread).</summary>
    event EventHandler<EngineSnapshot>? SnapshotChanged;

    /// <summary>A log line from the engine, the protocol session or a script.</summary>
    event EventHandler<LogEntry>? LogAdded;

    /// <summary>The most recent log lines, oldest first (bounded ring buffer).</summary>
    IReadOnlyList<LogEntry> GetRecentLog();

    /// <summary>Clears the in-memory log ring buffer.</summary>
    void ClearLog();

    /// <summary>A packet sent or received, only while <see cref="PacketCaptureEnabled"/> is true.</summary>
    event EventHandler<PacketEntry>? PacketLogged;

    /// <summary>The captured packets, oldest first (bounded ring buffer).</summary>
    IReadOnlyList<PacketEntry> GetRecentPackets();

    /// <summary>Clears the captured packets.</summary>
    void ClearPackets();

    /// <summary>Packet capture costs CPU and memory, so it is off until the packet logger turns it on.</summary>
    bool PacketCaptureEnabled { get; set; }

    /// <summary>Reconnect on its own after an unexpected disconnect (not after a logout, a refused login or "logged in elsewhere").</summary>
    bool AutoReconnect { get; set; }

    /// <summary>The public server list (no login needed), for the login screen's server picker.</summary>
    Task<IReadOnlyList<ServerChoice>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Web login, server choice and socket login. Completes when the character is loaded (or throws
    /// <see cref="EngineException"/> with a user-presentable message). Logs out first if already online.
    /// </summary>
    Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops a running script, disconnects and forgets the in-memory credentials.</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>Joins a map (needs to be online).</summary>
    Task JoinMapAsync(string map, string cell = "Enter", string pad = "Spawn", CancellationToken cancellationToken = default);

    /// <summary>Sends a raw packet exactly as typed (paced like every other packet). False when it was dropped.</summary>
    Task<bool> SendPacketAsync(string packet, CancellationToken cancellationToken = default);

    /// <summary>Asks the server for the bank contents (the bank list is empty until this ran once).</summary>
    Task LoadBankAsync(CancellationToken cancellationToken = default);

    /// <summary>Read-only lists for the inventory/bank/quests/drops screens, built from the live world model.</summary>
    WorldLists GetWorldLists();

    /// <summary>Whether scripts can run in this build, and why not.</summary>
    ScriptSupport ScriptSupport { get; }

    /// <summary>Selects the script to run next (does not compile or start it).</summary>
    void LoadScript(string scriptPath);

    /// <summary>Compiles (or loads from cache) and starts the loaded script, or <paramref name="scriptPath"/> when given.</summary>
    Task StartScriptAsync(string? scriptPath = null, CancellationToken cancellationToken = default);

    /// <summary>Asks the running script to stop and waits for it (bounded).</summary>
    Task StopScriptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The options a script declares (Skua's script options window), with their saved values. Compiles the script
    /// (or reuses its compile); refused while a script runs.
    /// </summary>
    /// <exception cref="EngineException">Not supported by this engine, a script is running, or the script does not compile.</exception>
    Task<ScriptOptionSet> GetScriptOptionsAsync(string scriptPath, CancellationToken cancellationToken = default) =>
        throw new EngineException("Script options are not available in this engine.");

    /// <summary>Saves edited script options where the script reads them.</summary>
    Task SaveScriptOptionsAsync(ScriptOptionSet options, CancellationToken cancellationToken = default) =>
        throw new EngineException("Script options are not available in this engine.");

    /// <summary>
    /// The built-in plugins' game-side halves (Daily Tracker, Butler), working on this engine's session. Null when this
    /// engine has none (no Skua host).
    /// </summary>
    Plugins.IPluginBackends? Plugins => null;

    /// <summary>
    /// Game and script events and the drop and junk actions of the Skua host (drop notifications, Notification
    /// Center, junk, Discord webhook). Null when this engine has none (no Skua host).
    /// </summary>
    Notifications.IGameEvents? GameEvents => null;

    /// <summary>
    /// Registers long work done for the user outside the game (a script repository sync): it shows in
    /// <see cref="EngineSnapshot.BackgroundWork"/> and keeps the process (the foreground service) alive until disposed.
    /// </summary>
    IDisposable BeginBackgroundWork(string description) => NoWork.Instance;

    private sealed class NoWork : IDisposable
    {
        public static readonly NoWork Instance = new();

        public void Dispose()
        {
        }
    }
}
