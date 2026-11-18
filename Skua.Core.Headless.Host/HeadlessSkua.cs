using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.GameProxy;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Options;
using Skua.Core.Scripts;
using Skua.Core.Scripts.Helpers;
using Skua.Core.Services;
using Skua.Core.Skills;
using System.Diagnostics;

namespace Skua.Core.Headless.Host;

/// <summary>Everything the platform gives the headless Skua host. All members are optional.</summary>
public sealed class HeadlessSkuaOptions
{
    /// <summary>
    /// Folder for Skua's own data (settings, options, script cache, AdvancedSkills.json, map cell cache). Skua finds it
    /// through the ApplicationData folder, which on Linux and Android is <c>$XDG_CONFIG_HOME</c>; this sets that variable
    /// for the process, so it must be given before anything touches Skua (the first <see cref="HeadlessSkuaSession.Create"/>).
    /// Null keeps the platform default (<c>~/.config/VibeSkua</c>).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Headless client settings (HTTP API, socket, client version, map cells).</summary>
    public HeadlessGameOptions Game { get; set; } = new();

    /// <summary>An HTTP API to use instead of a new one for game.aq.com (tests use a fake handler).</summary>
    public AqwWebApi? WebApi { get; set; }

    /// <summary>References scripts compile against. Default: every managed assembly next to the host and the runtime's.</summary>
    public IScriptReferenceProvider? ScriptReferences { get; set; }

    /// <summary>Default usings of scripts. Default: the desktop set.</summary>
    public IScriptNamespaceProvider? ScriptNamespaces { get; set; }

    /// <summary>How message boxes and questions are answered.</summary>
    public HeadlessDialogPolicy Dialogs { get; set; } = new();

    /// <summary>Opens a link (a browser on the device); null logs it instead.</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Receives every log line (Skua's script/debug/flash logs, the headless client, dialogs). Called on any thread.</summary>
    public Action<HeadlessLogEntry>? Log { get; set; }

    public TimeProvider? Time { get; set; }

    /// <summary>
    /// Version Skua reports to scripts (<c>bot.Version</c>, settings "ApplicationVersion"); null reports the real one.
    /// CoreBots' start-up check stops every script on a non-Windows OS unless the version is "1.8.5.0" (its marker for
    /// VibeSkua) or "1.4.4.4" (Skua), in which case it skips the Windows and update checks.
    /// </summary>
    public string? ApplicationVersionOverride { get; set; }
}

/// <summary>DI registration of Skua.Core on the headless client.</summary>
public static class HeadlessSkuaServiceCollectionExtensions
{
    /// <summary>
    /// Registers Skua.Core's scripting services (the same set Skua.App.Avalonia registers, without windows and plugins)
    /// with <see cref="HeadlessFlashUtil"/> as <see cref="IFlashUtil"/>, a <see cref="HeadlessGame"/>, the script
    /// compiler over <see cref="HeadlessSkuaOptions.ScriptReferences"/> and unattended UI services.
    /// </summary>
    public static IServiceCollection AddHeadlessSkua(this IServiceCollection services, HeadlessSkuaOptions? options = null)
    {
        options ??= new HeadlessSkuaOptions();
        Action<HeadlessLogEntry> log = options.Log ?? (_ => { });

        services.AddSingleton(options);
        services.AddCommonServices();
        services.AddTransient(Skua.Core.AppStartup.Services.CreateCompiler);
        services.AddSingleton<IScriptReferenceProvider>(options.ScriptReferences ?? HeadlessReferences.Default);
        services.AddSingleton<IScriptNamespaceProvider>(options.ScriptNamespaces ?? ScriptNamespaceProvider.Desktop);

        // Skua's scriptable objects (Skua.App.Avalonia AppStartup.Services.AddScriptableObjects).
        services.AddSingleton<IScriptInterface, ScriptInterface>();
        services.AddSingleton<ScriptManager>();
        services.AddSingleton<IScriptManager>(s => s.GetRequiredService<ScriptManager>());
        services.AddSingleton<IScriptStatus>(s => s.GetRequiredService<ScriptManager>());
        services.AddSingleton<IScriptInventoryHelper, ScriptInventoryHelper>();
        services.AddSingleton<IScriptInventory, ScriptInventory>();
        services.AddSingleton<IScriptHouseInv, ScriptHouseInv>();
        services.AddSingleton<IScriptTempInv, ScriptTempInv>();
        services.AddSingleton<IScriptBank, ScriptBank>();
        services.AddSingleton<IAdvancedSkillContainer, AdvancedSkillContainer>();
        services.AddSingleton<IUltraBossHelper, UltraBossHelper>();
        services.AddSingleton<IScriptCombat, ScriptCombat>();
        services.AddSingleton<IScriptKill, ScriptKill>();
        services.AddSingleton<IScriptHunt, ScriptHunt>();
        services.AddSingleton<IScriptSkill, ScriptSkill>();
        services.AddSingleton<IScriptAuto, ScriptAuto>();
        services.AddSingleton<IScriptSelfAuras, ScriptSelfAuras>();
        services.AddSingleton<IScriptTargetAuras, ScriptTargetAuras>();
        services.AddSingleton<IScriptFaction, ScriptFaction>();
        services.AddSingleton<IScriptMonster, ScriptMonster>();
        services.AddSingleton<IScriptPlayer, ScriptPlayer>();
        services.AddSingleton<IScriptQuest, ScriptQuest>();
        services.AddSingleton<IScriptBoost, ScriptBoost>();
        services.AddSingleton<IScriptShop, ScriptShop>();
        services.AddSingleton<IScriptDrop, ScriptDrop>();
        services.AddSingleton<IScriptMap, ScriptMap>();
        services.AddSingleton<IScriptServers, ScriptServers>();
        services.AddSingleton<IScriptEvent, ScriptEvent>();
        services.AddSingleton<IScriptSend, ScriptSend>();
        services.AddTransient<IScriptOptionContainer, ScriptOptionContainer>();
        services.AddTransient<IOptionContainer, OptionContainer>();
        services.AddSingleton<IScriptOption, ScriptOption>();
        services.AddSingleton<IScriptLite, ScriptLite>();
        services.AddSingleton<IScriptBotStats, ScriptBotStats>();
        services.AddSingleton<IScriptHandlers, ScriptHandlers>();
        services.AddSingleton<IScriptWait, ScriptWait>();
        services.AddSingleton<IScriptAccounts, ScriptAccounts>();
        services.AddSingleton<ICaptureProxy, CaptureProxy>();
        services.AddSingleton<IMapService, MapService>();
        services.AddSingleton<IQuestDataLoaderService, QuestDataLoaderService>();
        services.AddSingleton<IClientFilesService, ClientFilesService>();
        services.AddSingleton<IAuraMonitorService, AuraMonitorService>();
        services.AddSingleton<IJunkService, JunkService>();

        // Headless: the game, the bridge, and services that never wait for a person.
        services.AddSingleton(s => new HeadlessGame(options.Game, options.WebApi, options.Time));
        services.AddSingleton(s => new HeadlessFlashUtil(s.GetRequiredService<HeadlessGame>(), new Lazy<IScriptManager>(() => s.GetRequiredService<IScriptManager>())));
        services.AddSingleton<IFlashUtil>(s => s.GetRequiredService<HeadlessFlashUtil>());
        // Skua's server list over HTTPS (or the login reply's), never its plain-http fetch from content.aq.com.
        services.AddSingleton<IServerListProvider>(s => new HeadlessServerListProvider(s.GetRequiredService<HeadlessGame>(), log));
        services.AddSingleton<ISettingsService, HeadlessSettingsService>();
        services.AddSingleton<IDispatcherService, HeadlessDispatcher>();
        services.AddSingleton<IClipboardService, HeadlessClipboard>();
        services.AddSingleton<IWindowService, HeadlessWindowService>();
        services.AddSingleton<IFileDialogService, HeadlessFileDialogService>();
        services.AddSingleton<IHotKeyService, HeadlessHotKeys>();
        services.AddSingleton<ISoundService, HeadlessSound>();
        services.AddSingleton<IScreenshotService, HeadlessScreenshotService>();
        services.AddSingleton<IDialogService>(_ => new HeadlessDialogService(options.Dialogs, log));
        services.AddSingleton<IProcessService>(_ => new HeadlessProcessService(options.OpenLink, log));
        services.AddSingleton<ILogService>(_ => new HeadlessLogService(log));
        return services;
    }
}

/// <summary>The default script references: managed assemblies next to the host plus the runtime's own.</summary>
public static class HeadlessReferences
{
    private static readonly Lazy<IScriptReferenceProvider> s_default = new(() =>
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        IEnumerable<string> files = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Concat(Directory.EnumerateFiles(runtime, "*.dll"))
            .Where(IsManaged)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        return StreamScriptReferenceProvider.FromFiles(files.ToList());
    });

    public static IScriptReferenceProvider Default => s_default.Value;

    private static bool IsManaged(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Skua.Core resolves services through CommunityToolkit's <see cref="Ioc.Default"/>, which can be configured once per
/// process. The host configures it once with this forwarder and points it at the current session's container.
/// </summary>
internal static class HeadlessIoc
{
    private static readonly object s_gate = new();
    private static IServiceProvider? s_current;
    private static bool s_configured;

    public static void Use(IServiceProvider provider)
    {
        lock (s_gate)
        {
            s_current = provider;
            if (s_configured)
                return;
            Ioc.Default.ConfigureServices(new Forwarder());
            s_configured = true;
        }
    }

    public static void Release(IServiceProvider provider)
    {
        lock (s_gate)
        {
            if (ReferenceEquals(s_current, provider))
                s_current = null;
        }
    }

    private sealed class Forwarder : IServiceProvider
    {
        public object? GetService(Type serviceType) => Volatile.Read(ref s_current)?.GetService(serviceType);
    }
}

/// <summary>Where the session is.</summary>
public sealed record HeadlessSkuaStatus(
    HeadlessGameState State,
    bool InGame,
    string? Username,
    string? Server,
    string? Map,
    string? Cell,
    bool ScriptRunning,
    string? LoadedScript,
    int Kills,
    int Deaths,
    int QuestsCompleted,
    int Drops,
    int Relogins);

/// <summary>
/// One headless Skua: Skua.Core's services on <see cref="HeadlessFlashUtil"/>, logged in through Skua's own login,
/// relogin and server-selection code. Login, logout, script start/stop, state and events. One live session per
/// process (Skua.Core keeps process-wide state: <see cref="Ioc.Default"/>, the messengers, ScriptInterface.Instance).
/// </summary>
public sealed class HeadlessSkuaSession : IAsyncDisposable
{
    private static int s_live;
    private readonly ServiceProvider _provider;
    private readonly HeadlessLogService _logService;
    private readonly HeadlessLogService.TraceForward _trace;
    private readonly object _recipient = new();
    private bool _disposed;

    private HeadlessSkuaSession(ServiceProvider provider, HeadlessSkuaOptions options)
    {
        _provider = provider;
        Options = options;
        Game = provider.GetRequiredService<HeadlessGame>();
        Flash = provider.GetRequiredService<HeadlessFlashUtil>();
        _logService = (HeadlessLogService)provider.GetRequiredService<ILogService>();
        _trace = new HeadlessLogService.TraceForward(_logService);
        Trace.Listeners.Add(_trace);
    }

    public HeadlessSkuaOptions Options { get; }

    public IServiceProvider Services => _provider;

    /// <summary>Skua's script interface (what scripts get as <c>bot</c>).</summary>
    public IScriptInterface Bot => _provider.GetRequiredService<IScriptInterface>();

    public IScriptManager Scripts => _provider.GetRequiredService<IScriptManager>();

    public HeadlessGame Game { get; }

    public HeadlessFlashUtil Flash { get; }

    /// <summary>Bridge calls without a faithful answer.</summary>
    public FlashGapLog Gaps => Flash.Gaps;

    /// <summary>Login lifecycle changes (on any thread).</summary>
    public event EventHandler<HeadlessGameState>? StateChanged;

    /// <summary>A script started (true) or stopped (false).</summary>
    public event EventHandler<bool>? ScriptRunningChanged;

    /// <summary>A script threw.</summary>
    public event EventHandler<Exception>? ScriptError;

    /// <summary>
    /// Builds the services, points <see cref="Ioc.Default"/> at them, prepares Skua's folders and "loads" the game
    /// (skua.swf's requestLoadGame / loadClient / loaded), so Skua's timer runs. Not logged in yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another session is still alive in this process.</exception>
    public static HeadlessSkuaSession Create(HeadlessSkuaOptions? options = null)
    {
        options ??= new HeadlessSkuaOptions();
        if (Interlocked.CompareExchange(ref s_live, 1, 0) != 0)
            throw new InvalidOperationException("Only one HeadlessSkuaSession can be alive per process; dispose the previous one first.");
        try
        {
            if (options.DataDirectory is not null)
            {
                Directory.CreateDirectory(options.DataDirectory);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", options.DataDirectory);
            }
            options.Game.MapCacheDirectory ??= Path.Combine(Models.ClientFileSources.SkuaDIR, "cache", "mapcells");

            ServiceCollection services = new();
            services.AddHeadlessSkua(options);
            ServiceProvider provider = services.BuildServiceProvider();
            HeadlessIoc.Use(provider);

            ISettingsService settings = provider.GetRequiredService<ISettingsService>();
            settings.SetApplicationVersion();
            if (options.ApplicationVersionOverride is not null)
                settings.Set("ApplicationVersion", options.ApplicationVersionOverride);
            IClientFilesService files = provider.GetRequiredService<IClientFilesService>();
            files.CreateDirectories();
            SeedFile(Models.ClientFileSources.SkuaAdvancedSkillsFile);
            SeedFile(Models.ClientFileSources.SkuaQuestsFile);
            files.CreateFiles();

            HeadlessSkuaSession session = new(provider, options);
            session.Start();
            return session;
        }
        catch
        {
            Volatile.Write(ref s_live, 0);
            throw;
        }
    }

    // ClientFilesService.CreateFiles leaves File.Create's handle open when the file is missing, which Linux and Android
    // refuse to share with the reader that follows: create both files first.
    private static void SeedFile(string path)
    {
        if (File.Exists(path))
            return;
        string bundled = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path));
        if (File.Exists(bundled))
            File.Copy(bundled, path);
        else
            File.WriteAllText(path, "[]");
    }

    private void Start()
    {
        Game.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        Game.Log += (_, m) => Options.Log?.Invoke(new HeadlessLogEntry("game", m, DateTimeOffset.UtcNow));
        Flash.Log += m => Options.Log?.Invoke(new HeadlessLogEntry("game", m, DateTimeOffset.UtcNow));

        CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger m = CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger.Default;
        int status = (int)Skua.Core.Messaging.MessageChannels.ScriptStatus;
        m.Register<object, Skua.Core.Messaging.ScriptStartedMessage, int>(_recipient, status, (_, _) => ScriptRunningChanged?.Invoke(this, true));
        m.Register<object, Skua.Core.Messaging.ScriptStoppedMessage, int>(_recipient, status, (_, _) => ScriptRunningChanged?.Invoke(this, false));
        m.Register<object, Skua.Core.Messaging.ScriptErrorMessage, int>(_recipient, status, (_, msg) =>
            ScriptError?.Invoke(this, msg.Exception is System.Reflection.TargetInvocationException { InnerException: not null } t ? t.InnerException! : msg.Exception));
        // Nobody to ask "did the script end well?": answer for the user so ScriptManager does not wait.
        m.Register<object, Skua.Core.Messaging.ScriptStoppingRequestMessage, int>(_recipient, status, (_, msg) => msg.Reply(Task.FromResult<bool?>(msg.Exception is null)));

        IScriptInterface bot = Bot;   // subscribes to the bridge's events
        Flash.FlashCall += LoadWhenRequested;
        Flash.InitializeFlash();
        _ = bot;
    }

    // Skua.App.Avalonia's SkuaStartupHandler does this on Windows.
    private void LoadWhenRequested(string function, params object[] args)
    {
        if (function != "requestLoadGame")
            return;
        Flash.FlashCall -= LoadWhenRequested;
        Flash.Call("loadClient");
    }

    /// <summary>
    /// Logs in and joins a server through Skua's own login code (Servers.SetLoginInfo + EnsureRelogin), after an HTTP
    /// login that fills the server list Skua chooses from. True once the character is in game.
    /// </summary>
    /// <param name="server">Server name to prefer (Skua's relogin rules pick another when it is full or offline and
    /// the relogin fallback option is on). Null: Skua's Relogin Server option.</param>
    public async Task<bool> LoginAsync(string username, string password, string? server = null, CancellationToken cancellationToken = default)
    {
        await Game.Login(username, password).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Game.State != HeadlessGameState.ServerList)
            return false;
        Bot.Servers.SetLoginInfo(username, password);
        string target = server ?? Bot.Options.ReloginServer ?? Game.Servers.FirstOrDefault()?.Name ?? string.Empty;
        return await Bot.Servers.EnsureRelogin(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the script (if any) and logs out through Skua (Servers.Logout: intentional, no relogin).</summary>
    public async Task LogoutAsync()
    {
        if (Scripts.ScriptRunning)
            await Scripts.StopScript().ConfigureAwait(false);
        await Task.Run(() => Bot.Servers.Logout()).ConfigureAwait(false);
    }

    /// <summary>Compiles and starts a script file with Skua's ScriptManager. Null on success, else why it did not start.</summary>
    public async Task<Exception?> StartScriptAsync(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return new FileNotFoundException("No script at " + full, full);
        Scripts.SetLoadedScript(full);
        return await Scripts.StartScript().ConfigureAwait(false);
    }

    /// <summary>Stops the running script and waits for it to end.</summary>
    public async Task StopScriptAsync()
    {
        await Scripts.StopScript().ConfigureAwait(false);
    }

    public bool ScriptRunning => Scripts.ScriptRunning;

    public HeadlessSkuaStatus GetStatus()
    {
        World.AreaInfo area = Game.World.GetArea();
        IScriptBotStats stats = Bot.Stats;
        return new HeadlessSkuaStatus(
            Game.State,
            Game.IsInGame,
            Game.World.GetPlayer()?.Username ?? (Game.Username.Length > 0 ? Game.Username : null),
            Game.Session.Server?.Name,
            Game.IsInGame ? area.MapName : null,
            Game.IsInGame ? area.Cell : null,
            Scripts.ScriptRunning,
            string.IsNullOrEmpty(Scripts.LoadedScript) ? null : Scripts.LoadedScript,
            stats.Kills,
            stats.Deaths,
            stats.QuestsCompleted,
            stats.Drops,
            stats.Relogins);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (Scripts.ScriptRunning)
                await Scripts.StopScript().AsTask().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
        }
        CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger.Default.UnregisterAll(_recipient);
        Trace.Listeners.Remove(_trace);
        try
        {
            Bot.Servers.IsIntentionalLogout = true;
            Game.Disconnect();
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            (Bot as IDisposable)?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // ScriptInterface.Dispose stops its API wrapper by cancelling it, which surfaces as a cancellation.
        }
        Flash.Dispose();
        await Game.DisposeAsync().ConfigureAwait(false);
        HeadlessIoc.Release(_provider);
        try
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ScriptInterface.Dispose (again, from the container) cancels its wrapper the same way.
        }
        Volatile.Write(ref s_live, 0);
    }
}
