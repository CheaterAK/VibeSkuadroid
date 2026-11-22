using System.Security.Cryptography;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Scheduler;

namespace Skua.App.Droid.Core.GameView;

/// <summary>Where the account is.</summary>
public enum GameViewState
{
    /// <summary>The engine has the account (logged in or not); the game view is closed.</summary>
    Headless,

    /// <summary>Stopping the script or queue and logging the engine out.</summary>
    Preparing,

    /// <summary>The engine logged out; the game view was started and has not connected yet.</summary>
    Opening,

    /// <summary>The game view is open and has the account.</summary>
    Open,

    /// <summary>The view closed (or died): waiting for the server to release the session, then logging back in.</summary>
    Returning,
}

/// <summary>The switch's state for the UI.</summary>
/// <param name="State">Where the account is.</param>
/// <param name="Text">What is happening, for the Dashboard and Control page.</param>
public sealed record GameViewStatus(GameViewState State, string Text)
{
    public static GameViewStatus Closed { get; } = new(GameViewState.Headless, string.Empty);

    /// <summary>The script the switch stopped, offered to restart once the engine is back online (null: nothing to offer).</summary>
    public string? RestartScript { get; init; }

    /// <summary>How the last game view ended when that needs saying (memory limit, crash, did not open); null otherwise.</summary>
    public string? Notice { get; init; }

    /// <summary>The game view owns the account or is being opened or closed.</summary>
    public bool Busy => State != GameViewState.Headless;
}

/// <summary>What opening the game view will do, for the confirmation.</summary>
public sealed record GameViewPlan(bool Online, string? ScriptName, bool QueueRunning, string? QueueLine, string? AutoLoginAccount)
{
    /// <summary>Something runs that the switch stops: ask first.</summary>
    public bool NeedsConfirmation => ScriptName is not null || QueueRunning;

    public string Question => QueueRunning
        ? $"The script queue is running{(QueueLine is null ? string.Empty : $" ({QueueLine})")}. Opening the game view stops it and logs the bot out "
          + "(AQW allows one session per account). When you close the game view the bot logs back in and the queue continues at the same entry."
        : ScriptName is not null
            ? $"{ScriptName} is running. Opening the game view stops it and logs the bot out (AQW allows one session per account). "
              + "When you close the game view the bot logs back in and offers to start the script again."
            : Online
                ? "The bot logs out while the game view is open (AQW allows one session per account) and logs back in when you close it."
                : "The game view opens; log in there.";
}

/// <summary>What the game view gets when it connects: its settings and whether auto-login is offered (no secret).</summary>
public sealed record GameViewHello(string OptionsJson, string? AutoLoginAccount);

/// <summary>The stored login, handed to the game view only over the app's private link while it is open.</summary>
public sealed record GameViewCredentials(string Account, string Password)
{
    public override string ToString() => $"GameViewCredentials({Account}, <password redacted>)";
}

/// <summary>Starts and closes the game view on the platform (Android: an activity in the ":gameview" process).</summary>
public interface IGameViewLauncher
{
    /// <summary>
    /// Starts the game view for <paramref name="session"/> (a random id of this switch, not a secret of the account;
    /// the only thing passed). False when it could not be started.
    /// </summary>
    bool Launch(string session);

    /// <summary>Asks the view of <paramref name="session"/> to log out and close.</summary>
    void RequestClose(string session, string reason);
}

/// <summary>Timings of the switch.</summary>
public sealed record GameViewSwitchOptions
{
    /// <summary>How long the started view has to connect before the switch gives up and logs the engine back in.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Wait before logging back in when the view confirmed its game sockets were closed.</summary>
    public TimeSpan ReleaseDelayConfirmed { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Wait before logging back in when the view died or never confirmed (the server notices the dropped socket).</summary>
    public TimeSpan ReleaseDelayUnconfirmed { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>After asking the view to close: how long before the switch takes the account back without its answer.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often one open view may ask for the stored login (a retry after a failed login, a disconnect in the view).</summary>
    public int MaxCredentialRequests { get; init; } = 5;
}

/// <summary>
/// The game view switch. AQW allows one session per account (a second login kicks the first), so the real game in the
/// view and the headless engine take turns: opening stops the script or queue, logs the engine out on purpose and holds
/// it (no reconnect, no relogin), then starts the view; when the view closes, dies or never connects, the switch waits
/// for the server to release the session and logs the engine back in, then continues the queue or offers the script.
/// </summary>
/// <remarks>
/// Platform neutral: the view itself is behind <see cref="IGameViewLauncher"/>, and the platform's link to it calls
/// <see cref="Connect"/>, <see cref="TakeCredentials"/>, <see cref="ReportStage"/>, <see cref="ViewClosing"/> and
/// <see cref="ViewGone"/>. Every callback names the switch's session id; anything for another session is ignored.
/// Thread-safe; events are raised on the caller's thread.
/// </remarks>
public sealed class GameViewSwitch : IDisposable
{
    private readonly EngineHost _host;
    private readonly IGameViewLauncher _launcher;
    private readonly GameViewSwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private GameViewState _state = GameViewState.Headless;
    private string _text = string.Empty;
    private string? _session;
    private bool _queueSuspended;
    private string? _stoppedScript;
    private string? _restartScript;
    private string? _notice;
    private int _credentialRequests;
    private ITimer? _timer;
    private Task _returning = Task.CompletedTask;

    public GameViewSwitch(EngineHost host, IGameViewLauncher launcher, GameViewSwitchOptions? options = null, TimeProvider? time = null)
    {
        _host = host;
        _launcher = launcher;
        _options = options ?? new GameViewSwitchOptions();
        _time = time ?? TimeProvider.System;
    }

    public GameViewStatus Status
    {
        get
        {
            lock (_gate)
                return new GameViewStatus(_state, _text) { RestartScript = _restartScript, Notice = _notice };
        }
    }

    public GameViewState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>Raised on every change of <see cref="Status"/> (caller's thread).</summary>
    public event EventHandler<GameViewStatus>? Changed;

    /// <summary>The view's settings (stored with the app settings).</summary>
    public GameViewOptions Options => _host.Settings.Current.GameView ?? new GameViewOptions();

    public void SetOptions(GameViewOptions options) => _host.Settings.Update(s => s with { GameView = options.Normalized() });

    /// <summary>Completes when the last return to headless finished (tests).</summary>
    internal Task Returning
    {
        get
        {
            lock (_gate)
                return _returning;
        }
    }

    /// <summary>What <see cref="OpenAsync"/> would stop, for the confirmation.</summary>
    public GameViewPlan Plan()
    {
        EngineSnapshot s = _host.Engine.Snapshot;
        ScriptScheduler? queue = _host.Scheduler;
        bool queueRunning = queue?.IsRunning == true;
        return new GameViewPlan(
            s.Connection is ConnectionPhase.Online or ConnectionPhase.Reconnecting or ConnectionPhase.Connecting,
            s.Script.IsBusy ? s.Script.ScriptName ?? "A script" : null,
            queueRunning,
            queueRunning ? queue!.Status.NotificationLine : null,
            _host.RememberedAccount);
    }

    /// <summary>
    /// Hands the account to the game view: stops the queue (keeping its position) or the script, logs the engine out and
    /// holds it, then starts the view. If anything before the start fails, the engine gets the account back.
    /// </summary>
    /// <exception cref="EngineException">Already open, the script did not stop, or the view could not be started.</exception>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state != GameViewState.Headless)
                throw new EngineException("The game view is already open.");
            _state = GameViewState.Preparing;
            _text = "Preparing the game view...";
            _notice = null;
            _restartScript = null;
            _stoppedScript = null;
            _queueSuspended = false;
            _credentialRequests = 0;
        }
        Publish();

        IBotEngine engine = _host.Engine;
        string session;
        try
        {
            if (_host.Scheduler is ScriptScheduler queue && queue.IsRunning)
            {
                SetText("Pausing the script queue...");
                bool suspended = await queue.SuspendForGameViewAsync().ConfigureAwait(false);
                lock (_gate)
                    _queueSuspended = suspended;
            }
            EngineSnapshot s = engine.Snapshot;
            if (s.Script.IsBusy)
            {
                string? path = s.Script.ScriptPath;
                SetText("Stopping the script...");
                await engine.StopScriptAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                    _stoppedScript = _queueSuspended ? null : path;
            }
            if (engine.Snapshot.Script.IsBusy)
                throw new EngineException("The script did not stop; the game view was not opened.");

            SetText("Logging the bot out...");
            await engine.HoldForGameViewAsync(cancellationToken).ConfigureAwait(false);
            Log(EngineLogLevel.Info, "Opening the game view (" + Options.Describe() + ").");

            session = NewSession();
            lock (_gate)
            {
                _session = session;
                _state = GameViewState.Opening;
                _text = "Opening the game view...";
            }
            Publish();
        }
        catch (Exception e) when (e is EngineException or OperationCanceledException or InvalidOperationException)
        {
            await GiveBackAfterFailedOpenAsync(e is EngineException ? e.Message : "The game view was not opened.").ConfigureAwait(false);
            throw;
        }

        bool launched;
        try
        {
            launched = _launcher.Launch(session);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log(EngineLogLevel.Warning, $"Starting the game view failed: {e.GetType().Name}");
            launched = false;
        }
        if (!launched)
        {
            BeginReturn(session, "The game view could not be started.", confirmed: true, notice: "The game view could not be started.");
            throw new EngineException("The game view could not be started.");
        }
        Arm(_options.ConnectTimeout, () => ViewGone(session, "the game view did not open in time"));
    }

    /// <summary>The view connected over the platform's private link. Null when <paramref name="session"/> is not the open one.</summary>
    public GameViewHello? Connect(string session)
    {
        lock (_gate)
        {
            if (!IsCurrent(session) || _state is not (GameViewState.Opening or GameViewState.Open))
                return null;
            if (_state == GameViewState.Opening)
            {
                _state = GameViewState.Open;
                _text = "The game view is open: the bot is logged out until you close it.";
                Disarm();
            }
        }
        Publish();
        return new GameViewHello(Options.ToJson(), _host.RememberedAccount);
    }

    /// <summary>
    /// The stored login for the view's auto-login (decrypted from the Keystore vault now). Only for the open view of
    /// <paramref name="session"/>, only when a password is remembered, at most
    /// <see cref="GameViewSwitchOptions.MaxCredentialRequests"/> times per view. Never logged.
    /// </summary>
    public GameViewCredentials? TakeCredentials(string session)
    {
        lock (_gate)
        {
            if (!IsCurrent(session) || _state != GameViewState.Open || _credentialRequests >= _options.MaxCredentialRequests)
                return null;
            _credentialRequests++;
        }
        string? account = _host.RememberedAccount;
        if (account is null)
            return null;
        string? password = _host.Vault.Load(account, out _);
        if (string.IsNullOrEmpty(password))
            return null;
        Log(EngineLogLevel.Info, $"Auto-login in the game view as {account}.");
        return new GameViewCredentials(account, password);
    }

    /// <summary>A milestone of the view (login screen, in game, left the game).</summary>
    public void ReportStage(string session, string stage)
    {
        string? text = stage switch
        {
            "login_screen" => "The game view is at the login screen.",
            "in_game" => "The game view is in the game.",
            "left_game" => "The game view left the game.",
            _ => null,
        };
        lock (_gate)
        {
            if (!IsCurrent(session) || _state != GameViewState.Open || text is null)
                return;
            _text = text;
        }
        Publish();
    }

    /// <summary>
    /// The view is closing on its own (the user closed it, the memory guard, a renderer crash): it logged out in the game
    /// and, when <paramref name="socketsClosed"/>, its game sockets are closed. <paramref name="summary"/> (numbers only)
    /// goes to the Console.
    /// </summary>
    public void ViewClosing(string session, string reason, bool socketsClosed, string? summary)
    {
        if (summary is { Length: > 0 } && IsCurrentSession(session))
            Log(EngineLogLevel.Info, "Game view: " + Clip(summary, 400));
        BeginReturn(session, reason, socketsClosed, NoticeFor(reason));
    }

    /// <summary>The view went away without closing (its process died, it never connected, the link broke).</summary>
    public void ViewGone(string session, string reason) => BeginReturn(session, reason, confirmed: false, notice: $"The game view ended unexpectedly ({reason}); back to headless.");

    /// <summary>Asks the open view to close (Return to headless on the Dashboard or Control page).</summary>
    public void RequestClose(string reason = "closed from the app")
    {
        string? session;
        lock (_gate)
        {
            if (_state is not (GameViewState.Opening or GameViewState.Open))
                return;
            session = _session;
            _text = "Closing the game view...";
        }
        Publish();
        if (session is null)
            return;
        try
        {
            _launcher.RequestClose(session, reason);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log(EngineLogLevel.Warning, $"Asking the game view to close failed: {e.GetType().Name}");
        }
        Arm(_options.CloseTimeout, () => ViewGone(session, "the game view did not answer the close request"));
    }

    /// <summary>Starts the script the switch stopped (offered once the engine is back online).</summary>
    /// <exception cref="EngineException">Nothing to restart, not online yet, or the start failed.</exception>
    public async Task RestartScriptAsync(CancellationToken cancellationToken = default)
    {
        string path;
        lock (_gate)
            path = _restartScript ?? throw new EngineException("There is no script to restart.");
        if (_host.Engine.Snapshot.Connection != ConnectionPhase.Online)
            throw new EngineException("Wait until the bot is back online.");
        await _host.Engine.StartScriptAsync(path, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            _restartScript = null;
        Publish();
    }

    /// <summary>Drops the restart offer and the last notice.</summary>
    public void Dismiss()
    {
        lock (_gate)
        {
            _restartScript = null;
            _notice = null;
        }
        Publish();
    }

    private void BeginReturn(string session, string reason, bool confirmed, string? notice)
    {
        Task returning;
        lock (_gate)
        {
            if (!IsCurrent(session) || _state is not (GameViewState.Opening or GameViewState.Open))
                return;
            _state = GameViewState.Returning;
            _text = "The game view closed; waiting for the server to release the session...";
            _notice = notice;
            Disarm();
            returning = _returning = Task.Run(() => ReturnAsync(session, reason, confirmed));
        }
        Publish();
        _ = returning;
    }

    private async Task ReturnAsync(string session, string reason, bool confirmed)
    {
        Log(EngineLogLevel.Info, $"Game view closed ({reason}).");
        try
        {
            await Task.Delay(confirmed ? _options.ReleaseDelayConfirmed : _options.ReleaseDelayUnconfirmed, _time).ConfigureAwait(false);
            SetText("Logging the bot back in...");
            await _host.Engine.EndGameViewHoldAsync(reconnect: true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or ObjectDisposedException)
        {
            Log(EngineLogLevel.Warning, $"Taking the account back from the game view: {e.Message}");
        }
        FinishReturn(session);
    }

    private void FinishReturn(string session)
    {
        bool continueQueue;
        lock (_gate)
        {
            if (!IsCurrent(session))
                return;
            continueQueue = _queueSuspended;
            _restartScript = _stoppedScript;
            _state = GameViewState.Headless;
            _text = string.Empty;
            _session = null;
            _queueSuspended = false;
            _stoppedScript = null;
        }
        if (continueQueue)
            _host.Scheduler?.ContinueAfterGameView();
        Publish();
    }

    private async Task GiveBackAfterFailedOpenAsync(string why)
    {
        try
        {
            await _host.Engine.EndGameViewHoldAsync(reconnect: true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException or ObjectDisposedException)
        {
            Log(EngineLogLevel.Warning, $"Taking the account back: {e.Message}");
        }
        bool continueQueue;
        lock (_gate)
        {
            continueQueue = _queueSuspended;
            _restartScript = _stoppedScript;
            _state = GameViewState.Headless;
            _text = string.Empty;
            _notice = why;
            _session = null;
            _queueSuspended = false;
            _stoppedScript = null;
        }
        if (continueQueue)
            _host.Scheduler?.ContinueAfterGameView();
        Publish();
    }

    private static string? NoticeFor(string reason) =>
        reason.Contains("memory", StringComparison.OrdinalIgnoreCase) || reason.Contains("renderer", StringComparison.OrdinalIgnoreCase)
            ? $"The game view was closed: {reason}."
            : null;

    private bool IsCurrent(string session) => _session is not null && string.Equals(_session, session, StringComparison.Ordinal);

    private bool IsCurrentSession(string session)
    {
        lock (_gate)
            return IsCurrent(session);
    }

    private void SetText(string text)
    {
        lock (_gate)
            _text = text;
        Publish();
    }

    private void Arm(TimeSpan after, Action action)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => action(), null, after, Timeout.InfiniteTimeSpan);
        }
    }

    // Under _gate.
    private void Disarm()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Log(EngineLogLevel level, string message) => _host.Engine.WriteLog(level, "gameview", message);

    private void Publish()
    {
        GameViewStatus status = Status;
        try
        {
            Changed?.Invoke(this, status);
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
        {
            Log(EngineLogLevel.Warning, $"Game view status subscriber failed: {e.GetType().Name}");
        }
    }

    private static string NewSession() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "..." : s;

    public void Dispose()
    {
        lock (_gate)
            Disarm();
    }
}
