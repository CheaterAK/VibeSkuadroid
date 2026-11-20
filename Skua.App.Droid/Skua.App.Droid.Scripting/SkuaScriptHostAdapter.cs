using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.Skills;
using Skua.App.Droid.Plugins;
using Skua.Core.Headless;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Host;
using Skua.Core.Headless.Net;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Options;
using Skua.Core.Scripts;
using System.Reflection;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// <see cref="ISessionScriptHost"/> on Skua.Core through <see cref="HeadlessSkuaSession"/>: Skua's ScriptManager,
/// compiler and IScriptInterface API on the headless Flash bridge, with Skua's own login and relogin code.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="HeadlessSkuaSession"/> per process, created by <see cref="OpenSession"/> (the engine calls it once)
/// and kept until <see cref="DisposeAsync"/>. Its <see cref="HeadlessGame.Session"/> is the engine's session, so the
/// engine, the Flash bridge and every Skua service share one <see cref="AqwSession"/>. A logout keeps the session and
/// forgets the credentials in place (Skua's login info, the game's loginInfo and the session's token).
/// </para>
/// <para>
/// Every script and include assembly is loaded through <see cref="ScriptAssemblyStore"/> (ScriptManager's
/// <c>LoadContextFactory</c>), so an unchanged CoreBots is loaded once per process.
/// </para>
/// </remarks>
public sealed class SkuaScriptHostAdapter : ISessionScriptHost, IPluginBackendSource, IGameEventSource, ISkuaServicesSource, IFlashGapSource
{
    /// <summary>
    /// The version Skua reports to scripts (bot.Version and the "ApplicationVersion" setting). CoreBots'
    /// SkuaVersionCheckerAsync (auqw/Scripts, CoreBots.cs) returns early for "1.8.5.0", which CoreBots itself labels
    /// as VibeSkua; for any other version it requires Windows 10 and stops every script on a non-Windows OS. Scripts
    /// read bot.Version only there, in CoreBots' Windows .msi updater (skipped by that early return) and in a
    /// developer tool comparing it with "1.8.2".
    /// </summary>
    public const string ReportedSkuaVersion = "1.8.5.0";

    private static readonly TimeSpan ReloginWindow = TimeSpan.FromSeconds(90);

    private readonly ScriptReferenceSet? _references;
    private readonly ScriptAssemblyStore _assemblies;
    private readonly Action<HeadlessSkuaOptions>? _configure;
    private readonly StoreScriptLoadContext _loadContext;
    private readonly object _recipient = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _compileGate = new(1, 1);
    private readonly Dictionary<string, IScriptOptionContainer> _optionContainers = new(StringComparer.Ordinal);
    private HeadlessSkuaSession? _skua;
    private DroidPluginBackends? _plugins;
    private SkuaGameEvents? _gameEvents;
    private ScriptStatus _status = ScriptStatus.Idle;
    private string? _loaded;
    private string? _runError;
    private long _reloginTriggeredTicks = long.MinValue;
    private volatile bool _autoRelogin = true;
    private DateTime _skillSetsLoaded = DateTime.MinValue;

    /// <param name="references">Reference images from the APK (see <see cref="ScriptReferenceSet"/>); null uses
    /// <see cref="HeadlessReferences.Default"/> (the managed assemblies next to the app: Linux and the desktop preview).</param>
    /// <param name="assemblies">The process-wide content-addressed assembly cache.</param>
    /// <param name="scriptsDirectory">Equals Skua.Core's ClientFileSources.SkuaScriptsDIR (see AppPaths).</param>
    /// <param name="configure">Adjusts the host options before the session is created (tests point the game at a fake server).</param>
    public SkuaScriptHostAdapter(ScriptReferenceSet? references, ScriptAssemblyStore assemblies, string scriptsDirectory, Action<HeadlessSkuaOptions>? configure = null)
    {
        _references = references;
        _assemblies = assemblies;
        _configure = configure;
        _loadContext = new StoreScriptLoadContext(assemblies);
        ScriptsDirectory = scriptsDirectory;
        Support = references is { IsComplete: false }
            ? new ScriptSupport(false, $"Script references are incomplete ({string.Join(", ", references.Problems.Take(3))}).")
            : new ScriptSupport(true, null);
    }

    public string ScriptsDirectory { get; }

    public ScriptSupport Support { get; private set; }

    public ScriptStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>Distinct script/include images loaded in this process (never freed on Mono).</summary>
    public int LoadedAssemblyCount => _assemblies.Count;

    /// <summary>The Skua session (null before <see cref="OpenSession"/> or after it failed).</summary>
    public HeadlessSkuaSession? Skua => _skua;

    /// <summary>
    /// The built-in plugins (Daily Tracker, Butler) on this host's session, created with it. They always reach the
    /// current session's IScriptInterface, so they keep working across logouts (the session object is kept).
    /// </summary>
    public IPluginBackends? PluginBackends => _plugins;

    /// <summary>Skua's services on this host's session (the Tools pages: Advanced Skills, CoreBots options).</summary>
    public IServiceProvider? SkuaServices => _skua?.Services;

    /// <summary>The Flash bridge's gap log (Settings > Tools > Diagnostics).</summary>
    public IReadOnlyList<FlashGapRow> GetFlashGaps() =>
        _skua?.Gaps.Snapshot().Select(g => new FlashGapRow(g.Kind.ToString(), g.Name, g.ArgsShape, g.Count, g.FirstSeen, g.Detail)).ToList() ?? [];

    /// <summary>
    /// Skua's game and script messages, drop actions and junk list on this host's session (drop notifications,
    /// Notification Center, junk, Discord webhook). Created with the session.
    /// </summary>
    public IGameEvents? GameEvents => _gameEvents;

    /// <summary>Unattended answers to scripts' message boxes (logged as "dialog" lines).</summary>
    public HeadlessDialogPolicy Dialogs { get; } = DefaultDialogPolicy();

    public bool AutoRelogin
    {
        get => _autoRelogin;
        set => _autoRelogin = value;
    }

    public bool OwnsReconnect
    {
        get
        {
            HeadlessSkuaSession? s = _skua;
            if (s is null)
                return false;
            if (Status.IsBusy || s.ScriptRunning || s.Bot.Servers.IsReloginInProgress)
                return true;
            long since = Environment.TickCount64 - Interlocked.Read(ref _reloginTriggeredTicks);
            return since >= 0 && since < (long)ReloginWindow.TotalMilliseconds && !s.Game.IsInGame;
        }
    }

    public ScriptCounters? Counters
    {
        get
        {
            HeadlessSkuaSession? s = _skua;
            if (s is null || s.Game.State == HeadlessGameState.LoggedOut && string.IsNullOrEmpty(s.Game.Username))
                return null;
            HeadlessSkuaStatus st = s.GetStatus();
            return new ScriptCounters(st.Kills, st.Deaths, st.QuestsCompleted, st.Drops, st.Relogins);
        }
    }

    public event EventHandler<ScriptStatus>? StatusChanged;

    public event EventHandler<LogEntry>? Log;

    /// <summary>
    /// Nobody is there to answer, so: yes/no questions get "no" (scripts ask before doing something optional, such
    /// as CoreBots' "join our Discord?"); a choice of several buttons gets the declining one when offered (Cancel,
    /// No, Abort, Close: CoreBots' "update available, download now?" is declined), else the most private choice
    /// ("None" in CoreBots' data collection consent), else a harmless acknowledgement, else the first button; a
    /// single button (an "OK" notice) is that button. Confirmation and input dialogs are declined. Every question and
    /// its answer is logged as a "dialog" warning in the console.
    /// </summary>
    public static HeadlessDialogPolicy DefaultDialogPolicy()
    {
        HeadlessDialogPolicy policy = new() { YesNoAnswer = false };
        foreach (string b in new[] { "None", "Skip", "OK", "Ok", "Got it", "Understood", "Continue" })
            policy.PreferredButtons.Add(b);
        return policy;
    }

    public AqwSession? OpenSession()
    {
        if (_skua is not null)
            return _skua.Game.Session;
        HeadlessSkuaOptions options = new()
        {
            ScriptReferences = _references?.Provider,
            ScriptNamespaces = ScriptReferenceSet.Namespaces,
            ApplicationVersionOverride = ReportedSkuaVersion,
            Dialogs = Dialogs,
            Log = Forward,
            // The engine subscribes to the returned session's Log (source "session", with its level): do not
            // forward those lines a second time as "game".
            LogSessionLines = false,
            // Skua's AdvancedSkillContainer would download AdvancedSkills.json from GitHub when it is created and
            // merge it into UserAdvancedSkills.json in the background. The app's script sync refreshes that file and
            // SkillSetStore merges it before each run (keeping the user's edits): one writer only.
            ConfigureSettings = settings =>
            {
                settings.Set("CheckAdvanceSkillSetsUpdates", false);
                settings.Set("AutoUpdateAdvanceSkillSetsUpdates", false);
            },
        };
        _configure?.Invoke(options);
        try
        {
            HeadlessSkuaSession skua = HeadlessSkuaSession.Create(options);
            skua.Services.GetRequiredService<ScriptManager>().LoadContextFactory = _loadContext.BeginCompile;
            skua.ScriptRunningChanged += OnScriptRunningChanged;
            skua.ScriptError += OnScriptError;
            skua.Scripts.PropertyChanged += OnScriptManagerPropertyChanged;
            StrongReferenceMessenger.Default.Register<object, ReloginTriggeredMessage, int>(_recipient, (int)MessageChannels.GameEvents,
                (_, m) =>
                {
                    Interlocked.Exchange(ref _reloginTriggeredTicks, Environment.TickCount64);
                    Emit(EngineLogLevel.Warning, "scripts", m.WasKicked ? "Kicked: Skua's relogin runs." : "Disconnected: Skua's relogin runs.");
                });
            _skua = skua;
            _plugins = new DroidPluginBackends(() => _skua?.Bot, () => _skua?.Services);
            _gameEvents = new SkuaGameEvents(() => _skua?.Bot, () => _skua?.Services, warning => Emit(EngineLogLevel.Warning, "drops", warning));
            return skua.Game.Session;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or TypeInitializationException)
        {
            Support = new ScriptSupport(false, $"The Skua script host did not start: {e.GetType().Name}: {e.Message}");
            Emit(EngineLogLevel.Error, "scripts", Support.Reason!);
            return null;
        }
    }

    public void Attach(AqwSession session)
    {
        // Only used without OpenSession (the engine's legacy path); this host owns its session.
    }

    public void Load(string scriptPath)
    {
        lock (_gate)
        {
            _loaded = Path.GetFullPath(scriptPath);
            if (!_status.IsBusy)
                _status = new ScriptStatus(ScriptRunState.Idle, _loaded, null, null);
        }
        StatusChanged?.Invoke(this, Status);
    }

    public async Task<string> LoginAsync(LoginRequest request, Action<string> connecting, CancellationToken cancellationToken)
    {
        HeadlessSkuaSession s = RequireSkua();
        s.Game.Session.Options.ClientVersion = string.IsNullOrWhiteSpace(request.ClientVersion) ? null : request.ClientVersion.Trim();
        string user = request.Username.Trim();

        // The game's login screen: HTTP login, then the server list.
        await s.Game.Login(user, request.Password).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (s.Game.State != HeadlessGameState.ServerList)
            throw new EngineException("Login failed: " + (s.Game.ModalMessage ?? "the server refused the login."));
        GameServer server = s.Game.Session.PickServer(string.IsNullOrWhiteSpace(request.Server) ? null : request.Server.Trim());
        connecting(server.Name);

        // Skua's own login (what its relogin uses later): login info, then EnsureRelogin picks and joins the server.
        s.Bot.Servers.SetLoginInfo(user, request.Password);
        bool joined = await s.Bot.Servers.EnsureRelogin(server.Name, cancellationToken).ConfigureAwait(false);
        if (!joined || !s.Game.IsInGame)
            throw new EngineException("Connection failed: " + (s.Game.ConnectionDetail ?? s.Game.ModalMessage ?? $"could not join {server.Name}."));
        return s.Game.Session.Server?.Name ?? server.Name;
    }

    public async Task ReconnectAsync(string? server, CancellationToken cancellationToken)
    {
        HeadlessSkuaSession s = RequireSkua();
        if (string.IsNullOrEmpty(s.Game.Password))
            throw new EngineException("No login to reconnect with.");
        bool joined = await s.Bot.Servers.EnsureRelogin(server ?? s.Bot.Options.ReloginServer ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!joined || !s.Game.IsInGame)
            throw new EngineException(s.Game.ConnectionDetail ?? s.Game.ModalMessage ?? "Skua's relogin did not get back in game.");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        HeadlessSkuaSession? s = _skua;
        if (s is null)
            return;
        try
        {
            if (s.Game.IsConnected || s.ScriptRunning)
                await s.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (s.Game.IsConnected && DateTime.UtcNow < end)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened above, nothing that could log in again stays in memory.
            s.Bot.Servers.SetLoginInfo(string.Empty, string.Empty);
            s.Game.ForgetLogin();
            Interlocked.Exchange(ref _reloginTriggeredTicks, long.MinValue);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => StartAsync(ScriptStartOptions.Default, cancellationToken);

    /// <summary>
    /// Starts the loaded script. <see cref="ScriptStartOptions.OptionStorage"/> becomes Skua's OverrideStorage and
    /// <see cref="ScriptStartOptions.Silent"/> its SilentConfig, and both stay set until the next start: Skua's relogin
    /// restarts the script by itself (ScriptManager.StartScript reads them again), and that restart must read the same
    /// option profile. A manual start clears them. (The desktop scheduler clears OverrideStorage when the script stops, so
    /// its relogin restart falls back to the script's own options; here the entry keeps its profile.)
    /// </summary>
    public async Task StartAsync(ScriptStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Support.Available)
            throw new EngineException(Support.Reason ?? "Scripts are not available.");
        HeadlessSkuaSession s = RequireSkua();
        string path;
        lock (_gate)
        {
            path = _loaded ?? throw new EngineException("Choose a script first.");
            if (_status.IsBusy)
                throw new EngineException("A script is already running.");
            _runError = null;
            _status = new ScriptStatus(ScriptRunState.Compiling, path, null, "Compiling...");
        }
        StatusChanged?.Invoke(this, Status);
        if (!File.Exists(path))
        {
            SetStatus(new ScriptStatus(ScriptRunState.Faulted, path, null, "The script file does not exist."));
            throw new EngineException($"No script at {path}.");
        }

        Exception? failure;
        await _compileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReloadSkillSetsIfUpdated(s);
            s.Scripts.OverrideStorage = string.IsNullOrWhiteSpace(options.OptionStorage) ? null : options.OptionStorage;
            s.Scripts.SilentConfig = options.Silent;
            failure = await s.StartScriptAsync(path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure = e;
        }
        finally
        {
            _compileGate.Release();
        }
        if (failure is null)
            return;

        string message = Describe(failure);
        Emit(EngineLogLevel.Error, "scripts", $"{Path.GetFileName(path)} did not start: {failure.Message}");
        SetStatus(new ScriptStatus(ScriptRunState.Faulted, path, null, message));
        throw new EngineException(message, failure);
    }

    public bool CanPause => _skua is not null;

    /// <summary>
    /// Skua's own pause (ScriptManager.SetPausedAsync, the desktop's Pause button and hotkey): the skill timer pauses and
    /// the script thread blocks on ScriptPauseEvent at its next Bot.Sleep or Bot.Wait call; resuming stops Skua's auto
    /// attack and releases the thread. Nothing in it needs Flash or a window, so it is used as is. Skua's timer thread
    /// (relogin watchdog, handlers) and the session (keepalive, AFK clearing) keep running.
    /// </summary>
    public async Task<bool> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        HeadlessSkuaSession s = RequireSkua();
        if (Status.State != ScriptRunState.Running || !s.ScriptRunning)
            throw new EngineException("No script is running.");
        bool applied = await s.Scripts.SetPausedAsync(paused).WaitAsync(cancellationToken).ConfigureAwait(false);
        SyncPaused(s);
        if (applied && !paused)
            Emit(EngineLogLevel.Info, "scripts", $"{Path.GetFileName(Status.ScriptPath)} resumed.");
        return applied;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        HeadlessSkuaSession? s = _skua;
        if (s is null)
            return;
        lock (_gate)
        {
            if (_status.State is ScriptRunState.Running)
                _status = _status with { State = ScriptRunState.Stopping, Message = "Stopping...", Paused = false };
        }
        StatusChanged?.Invoke(this, Status);
        await s.StopScriptAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!s.ScriptRunning)
        {
            lock (_gate)
            {
                if (_status.IsBusy)
                    _status = _status with { State = ScriptRunState.Idle, Message = "Stopped." };
            }
            StatusChanged?.Invoke(this, Status);
        }
    }

    public Task<ScriptOptionSet> GetOptionsAsync(string scriptPath, CancellationToken cancellationToken) =>
        GetOptionsAsync(scriptPath, null, cancellationToken);

    /// <summary>
    /// As the desktop scheduler's "edit options" of a queue entry: Skua's OverrideStorage is set to
    /// <paramref name="optionStorage"/> while the script's option container is built, so the options are read from (and
    /// saved to) that storage. Null reads the script's own storage.
    /// </summary>
    public async Task<ScriptOptionSet> GetOptionsAsync(string scriptPath, string? optionStorage, CancellationToken cancellationToken)
    {
        HeadlessSkuaSession s = RequireSkua();
        if (!Support.Available)
            throw new EngineException(Support.Reason ?? "Scripts are not available.");
        string path = Path.GetFullPath(scriptPath);
        if (!File.Exists(path))
            throw new EngineException($"No script at {path}.");
        await _compileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s.ScriptRunning || Status.IsBusy)
                throw new EngineException("Stop the script before editing options.");
            IScriptManager manager = s.Scripts;
            string previous = manager is ScriptManager { LoadedScript: { Length: > 0 } p } ? p : path;
            string? previousStorage = manager.OverrideStorage;
            object? script;
            try
            {
                manager.SetLoadedScript(path);
                string source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                script = await Task.Run(() => manager.Compile(source), cancellationToken).ConfigureAwait(false);
                manager.OverrideStorage = string.IsNullOrWhiteSpace(optionStorage) ? null : optionStorage;
                manager.LoadScriptConfig(script);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EngineException)
            {
                throw new EngineException(Describe(e), e);
            }
            finally
            {
                manager.OverrideStorage = previousStorage;
                manager.SetLoadedScript(previous);
            }
            IScriptOptionContainer? container = manager.Config;
            if (script is null || container is null)
                throw new EngineException("The script has no options.");
            lock (_optionContainers)
                _optionContainers[ContainerKey(path, container.Storage)] = container;
            return ToOptionSet(path, container);
        }
        finally
        {
            _compileGate.Release();
        }
    }

    public async Task SaveOptionsAsync(ScriptOptionSet options, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(options.ScriptPath);
        string key = ContainerKey(path, options.Storage);
        IScriptOptionContainer? container;
        lock (_optionContainers)
            _optionContainers.TryGetValue(key, out container);
        if (container is null)
        {
            // Overriding with the script's own storage name reads the same file as no override.
            await GetOptionsAsync(path, options.Storage, cancellationToken).ConfigureAwait(false);
            lock (_optionContainers)
                container = _optionContainers[key];
        }
        List<string> problems = new();
        foreach (ScriptOptionItem item in options.Items)
        {
            if (item.Transient)
                continue;
            IOption? option = Find(container, item.Category, item.Name);
            if (option is null)
                continue;
            string? value = Normalize(option, item.Value, out string? problem);
            if (problem is not null)
            {
                problems.Add($"{item.Title}: {problem}");
                continue;
            }
            container.OptionValues[option] = value!;
        }
        if (problems.Count > 0)
            throw new EngineException("Not saved. " + string.Join("; ", problems));
        await Task.Run(container.Save, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        HeadlessSkuaSession? s = Interlocked.Exchange(ref _skua, null);
        StrongReferenceMessenger.Default.UnregisterAll(_recipient);
        Interlocked.Exchange(ref _plugins, null)?.Dispose();
        Interlocked.Exchange(ref _gameEvents, null)?.Dispose();
        if (s is null)
            return;
        s.ScriptRunningChanged -= OnScriptRunningChanged;
        s.ScriptError -= OnScriptError;
        s.Scripts.PropertyChanged -= OnScriptManagerPropertyChanged;
        s.Services.GetRequiredService<ScriptManager>().LoadContextFactory = null;
        await s.DisposeAsync().ConfigureAwait(false);
    }

    private static string ContainerKey(string path, string storage) => path + "\n" + storage;

    // ------------------------------------------------------------------ Skua events

    private void OnScriptManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScriptManager.ScriptPaused) && _skua is HeadlessSkuaSession s)
            SyncPaused(s);
    }

    // Mirrors Skua's applied pause state into the status (only while running; a stop resets Skua's pause itself).
    private void SyncPaused(HeadlessSkuaSession s)
    {
        bool paused = s.Scripts.ScriptPaused;
        ScriptStatus status;
        lock (_gate)
        {
            if (_status.State != ScriptRunState.Running || _status.Paused == paused)
                return;
            _status = _status with { Paused = paused };
            status = _status;
        }
        StatusChanged?.Invoke(this, status);
        // Skua also clears its pause when a paused script stops: only the pause is logged here, a resume by SetPausedAsync.
        if (paused)
            Emit(EngineLogLevel.Info, "scripts", $"{Path.GetFileName(status.ScriptPath)} paused.");
    }

    private void OnScriptRunningChanged(object? sender, bool running)
    {
        HeadlessSkuaSession? s = _skua;
        if (running)
        {
            // Skua announces a start before the script thread runs: apply the app's reconnect choice as the script's
            // relogin option (desktop: the AutoRelogin box). The script may change it; Skua resets it when it stops.
            if (s is not null)
                s.Bot.Options.AutoRelogin = _autoRelogin;
            string? path = s?.Scripts.LoadedScript is { Length: > 0 } p ? p : _loaded;
            SetStatus(new ScriptStatus(ScriptRunState.Running, path, DateTimeOffset.UtcNow, null));
            Emit(EngineLogLevel.Info, "scripts", $"{Path.GetFileName(path)} started.");
            return;
        }
        string? error;
        lock (_gate)
            error = _runError;
        ScriptStatus current = Status;
        SetStatus(error is null
            ? new ScriptStatus(ScriptRunState.Idle, current.ScriptPath, null, "Stopped.")
            : new ScriptStatus(ScriptRunState.Faulted, current.ScriptPath, null, error));
        Emit(EngineLogLevel.Info, "scripts", $"{Path.GetFileName(current.ScriptPath)} stopped.");
    }

    private void OnScriptError(object? sender, Exception e)
    {
        string message = Describe(e);
        lock (_gate)
            _runError = message;
        Emit(EngineLogLevel.Error, "scripts", $"Script error: {e.GetType().Name}: {e.Message}");
        if (e.StackTrace is string trace)
            Emit(EngineLogLevel.Debug, "scripts", string.Join(Environment.NewLine, trace.Split('\n').Take(12)));
    }

    // The repository sync refreshes AdvancedSkills.json (the community skill sets). Before a run, bring Skua's file
    // (UserAdvancedSkills.json) up to date with it, keeping the user's edits (SkillSetStore.UpdateFromCommunity), and
    // reload Skua's container when that file changed since it was last loaded (also after an edit in Advanced Skills).
    private void ReloadSkillSetsIfUpdated(HeadlessSkuaSession s)
    {
        try
        {
            SkillSetStore store = new(ClientFileSources.SkuaDIR);
            bool merged = store.UpdateFromCommunity();
            DateTime stamp = File.Exists(store.UserFile) ? File.GetLastWriteTimeUtc(store.UserFile) : DateTime.MinValue;
            if (!merged && stamp == _skillSetsLoaded)
                return;
            s.Services.GetRequiredService<IAdvancedSkillContainer>().LoadSkills();
            _skillSetsLoaded = stamp;
            if (merged)
                Emit(EngineLogLevel.Info, "scripts", "Skill sets updated from the synced AdvancedSkills.json (your edits are kept).");
        }
        catch (EngineException e)
        {
            Emit(EngineLogLevel.Warning, "scripts", $"Could not update the skill sets: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        {
            Emit(EngineLogLevel.Warning, "scripts", $"Could not reload the skill sets: {e.Message}");
        }
    }

    private void Forward(HeadlessLogEntry entry)
    {
        (EngineLogLevel level, string source) = entry.Source switch
        {
            "script" => (EngineLogLevel.Info, "script"),
            "dialog" => (EngineLogLevel.Warning, "dialog"),
            "debug" => (entry.Message.Contains("Error", StringComparison.OrdinalIgnoreCase) ? EngineLogLevel.Warning : EngineLogLevel.Debug, "skua"),
            "flash" => (EngineLogLevel.Debug, "flash"),
            _ => (EngineLogLevel.Debug, "game"),
        };
        Log?.Invoke(this, new LogEntry(entry.At, level, source, entry.Message));
    }

    private void Emit(EngineLogLevel level, string source, string message) =>
        Log?.Invoke(this, new LogEntry(DateTimeOffset.UtcNow, level, source, message));

    private void SetStatus(ScriptStatus status)
    {
        lock (_gate)
            _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private HeadlessSkuaSession RequireSkua() =>
        _skua ?? throw new EngineException(Support.Reason ?? "The Skua script host is not running.");

    private static string Describe(Exception e)
    {
        Exception inner = e is TargetInvocationException { InnerException: not null } t ? t.InnerException! : e;
        string text = inner.Message;
        int newline = text.IndexOf('\n');
        string first = newline < 0 ? text : text[..newline].TrimEnd('\r');
        if (first.Length > 300)
            first = first[..300] + "...";
        return inner switch
        {
            ScriptCompileException => "Compile error: " + first,
            _ => $"{inner.GetType().Name}: {first}",
        };
    }

    // ------------------------------------------------------------------ options

    private static ScriptOptionSet ToOptionSet(string path, IScriptOptionContainer container)
    {
        List<ScriptOptionItem> items = new();
        foreach (IOption o in container.Options)
            items.Add(ToItem("Options", o, container));
        foreach (KeyValuePair<string, List<IOption>> group in container.MultipleOptions)
        {
            foreach (IOption o in group.Value)
                items.Add(ToItem(group.Key, o, container));
        }
        return new ScriptOptionSet(path, container.Storage, items);
    }

    private static ScriptOptionItem ToItem(string category, IOption o, IOptionContainer container)
    {
        Type t = Nullable.GetUnderlyingType(o.Type) ?? o.Type;
        string value = container.GetDirect(o);
        (ScriptOptionKind kind, IReadOnlyList<string> choices) = t switch
        {
            _ when t == typeof(bool) => (ScriptOptionKind.Toggle, (IReadOnlyList<string>)Array.Empty<string>()),
            _ when t.IsEnum => (ScriptOptionKind.Choice, Enum.GetNames(t)),
            _ when IsNumber(t) => (ScriptOptionKind.Number, Array.Empty<string>()),
            _ => (ScriptOptionKind.Text, Array.Empty<string>()),
        };
        if (kind == ScriptOptionKind.Choice)
            value = Enum.TryParse(t, OptionContainer.ToEnumText(value), true, out object? parsed) ? parsed!.ToString()! : value;
        if (kind == ScriptOptionKind.Toggle)
            value = bool.TryParse(value, out bool b) ? (b ? "True" : "False") : "False";
        return new ScriptOptionItem(category, o.Name, o.DisplayName, o.Description, kind, value, choices, o.Transient);
    }

    private static bool IsNumber(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong)
        || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static IOption? Find(IOptionContainer container, string category, string name) =>
        category == "Options"
            ? container.Options.Find(o => o.Name == name)
            : container.MultipleOptions.TryGetValue(category, out List<IOption>? list) ? list.Find(o => o.Name == name) : null;

    // The text Skua stores, or a problem; parsed the way OptionContainer.Get<T> will read it back.
    private static string? Normalize(IOption option, string? input, out string? problem)
    {
        problem = null;
        Type t = Nullable.GetUnderlyingType(option.Type) ?? option.Type;
        string value = input?.Trim() ?? string.Empty;
        if (t == typeof(string))
            return input ?? string.Empty;
        if (t.IsEnum)
        {
            string text = OptionContainer.ToEnumText(value);
            if (Enum.TryParse(t, text, true, out object? e))
                return OptionContainer.ToEnumText(e!.ToString()!);
            problem = $"\"{value}\" is not one of {string.Join(", ", Enum.GetNames(t))}";
            return null;
        }
        if (t == typeof(bool))
        {
            if (bool.TryParse(value, out bool b))
                return b.ToString();
            problem = "not true or false";
            return null;
        }
        try
        {
            object converted = Convert.ChangeType(value, t, System.Globalization.CultureInfo.CurrentCulture);
            return Convert.ToString(converted, System.Globalization.CultureInfo.CurrentCulture); // OptionContainer.Get<T> parses with the current culture
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
        {
            problem = $"\"{value}\" is not a valid {t.Name}";
            return null;
        }
    }
}
