using System.Threading.Channels;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>
/// Process-wide wiring of the notification features (one per engine, created with it, alive without any UI): the
/// Notification Center history, drop watch alerts (system notifications), automatic rejection of junk drops and the
/// Discord webhook, all fed by the Skua host's <see cref="IGameEvents"/>.
/// </summary>
/// <remarks>
/// Events arrive on the Flash bridge or the script thread. The handler only queues them; one background worker
/// writes the history, posts alerts and hands webhook payloads to <see cref="DiscordWebhookSender"/>, whose own queue
/// does the network I/O. Nothing here ever makes the engine or a script wait.
/// </remarks>
public sealed class NotificationHub : IAsyncDisposable
{
    private static readonly TimeSpan WebhookDropCooldown = TimeSpan.FromSeconds(30);

    private readonly IBotEngine _engine;
    private readonly IDropAlertSink _alerts;
    private readonly SecretScrubber? _scrubber;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Channel<GameEvent> _events = Channel.CreateBounded<GameEvent>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Dictionary<int, DateTimeOffset> _lastWebhookDrop = new();
    private readonly Task _worker;
    private readonly IGameEvents? _game;

    // Latest state from the engine's snapshots (any thread writes, the worker reads).
    private volatile string? _characterName;
    private volatile string? _map;
    private long _gold = long.MinValue;
    private volatile ScriptCounters? _counters;

    // Worker only.
    private DateTimeOffset? _scriptStartedAt;
    private string? _runningScript;
    private long _startGold = long.MinValue;
    private int _processed;

    public NotificationHub(
        IBotEngine engine,
        NotificationSettingsStore settings,
        NotificationHistory history,
        IDropAlertSink alerts,
        WebhookUrlVault vault,
        DiscordWebhookSender sender,
        SecretScrubber? scrubber = null,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        _engine = engine;
        Settings = settings;
        History = history;
        _alerts = alerts;
        Vault = vault;
        Sender = sender;
        _scrubber = scrubber;
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _game = engine.GameEvents;

        LoadWebhookUrl();
        ApplyJunkSettings(settings.Current);
        settings.Changed += OnSettingsChanged;
        engine.SnapshotChanged += OnSnapshot;
        OnSnapshot(this, engine.Snapshot);
        if (_game is not null)
            _game.Raised += OnGameEvent;
        _worker = Task.Run(RunAsync);
    }

    public NotificationSettingsStore Settings { get; }

    public NotificationHistory History { get; }

    /// <summary>The encrypted webhook URL.</summary>
    public WebhookUrlVault Vault { get; }

    public DiscordWebhookSender Sender { get; }

    /// <summary>The Skua host's events and drop actions; null without a Skua host.</summary>
    public IGameEvents? Game => _game;

    /// <summary>System notifications for watched drops.</summary>
    public IDropAlertSink Alerts => _alerts;

    /// <summary>A webhook URL is loaded (it may still be refused by Discord).</summary>
    public bool WebhookConfigured => Sender.Url is not null;

    /// <summary>Events handled so far (tests wait on it).</summary>
    public int Processed => Volatile.Read(ref _processed);

    /// <summary>Validates, encrypts and stores the URL, and uses it from now on.</summary>
    public bool SetWebhookUrl(string? text, out string error)
    {
        if (!DiscordWebhookUrl.TryParse(text, out DiscordWebhookUrl? url, out error))
            return false;
        if (!Vault.IsAvailable)
        {
            error = "No secure storage on this device: the webhook URL cannot be stored.";
            return false;
        }
        try
        {
            Vault.Save(url!);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = $"The webhook URL could not be stored ({e.GetType().Name}).";
            return false;
        }
        Use(url);
        return true;
    }

    /// <summary>Deletes the stored URL; nothing is sent any more.</summary>
    public void ForgetWebhook()
    {
        Vault.Forget();
        Sender.Url = null;
    }

    /// <summary>Re-reads the stored URL (when it could not be decrypted at start).</summary>
    public void LoadWebhookUrl()
    {
        if (!Vault.HasUrl)
            return;
        DiscordWebhookUrl? url = Vault.Load(out SecretStatus status);
        if (url is not null)
            Use(url);
        else if (status == SecretStatus.Unavailable)
            _log("The Discord webhook URL could not be read right now; it is kept.");
    }

    private void Use(DiscordWebhookUrl? url)
    {
        if (url is not null)
        {
            // Redacted from logs and crash reports from now on, should it ever appear in one.
            _scrubber?.Add(url.Uri.AbsoluteUri);
            _scrubber?.Add(url.Token);
        }
        Sender.Url = url;
    }

    /// <summary>Sends the test card and waits for Discord's answer (the settings page's button; never the engine).</summary>
    public Task<WebhookResult> SendWebhookTestAsync() => Sender.SendAsync(WebhookPayloads.Test());

    /// <summary>
    /// The scheduler's "Scheduler Paused" card (the desktop scheduler sends it while it waits for the account to
    /// reconnect). Returns at once.
    /// </summary>
    public void NotifySchedulerPaused()
    {
        NotificationSettings s = Settings.Current;
        if (s.WebhookEnabled && s.WebhookNotifySchedulerPaused)
            Sender.Enqueue(WebhookPayloads.SchedulerPaused());
        History.Add(new NotificationEntry(_time.GetUtcNow(), NotificationKind.Warning, "Scheduler paused: waiting for the account to reconnect."));
    }

    /// <summary>Posts a sample alert (the drop watch page's test button).</summary>
    public void PostTestAlert() =>
        _alerts.Post(new DropAlert(0, "Test item", 1, Obtained: false, Settings.Current.ShowNamesOnLockScreen));

    /// <summary>Who the cards name: the character when allowed, else the desktop's anonymous name.</summary>
    public string WebhookName => Settings.Current.WebhookIncludeCharacterName && _characterName is string name ? name : WebhookPayloads.AnonymousName;

    /// <summary>Queues an event as if the Skua host had raised it (tests, and events the engine itself knows).</summary>
    public void Post(GameEvent e) => _events.Writer.TryWrite(e);

    private void OnGameEvent(object? sender, GameEvent e) => _events.Writer.TryWrite(e);

    private void OnSnapshot(object? sender, EngineSnapshot s)
    {
        if (s.Character is CharacterState c)
        {
            // Kept across disconnects (the desktop's CachedUsername), forgotten on logout.
            if (!string.IsNullOrWhiteSpace(c.Name))
                _characterName = c.Name;
            if (!string.IsNullOrWhiteSpace(c.Map))
                _map = c.Map;
            Interlocked.Exchange(ref _gold, c.Gold);
        }
        else if (s.Connection == ConnectionPhase.Offline)
        {
            _characterName = null;
            _map = null;
        }
        if (s.ScriptCounters is not null)
            _counters = s.ScriptCounters;
    }

    private void OnSettingsChanged(object? sender, NotificationSettings s) => ApplyJunkSettings(s);

    private void ApplyJunkSettings(NotificationSettings s)
    {
        if (_game is null)
            return;
        IReadOnlyList<string> watched = s.WatchedItems;
        _game.Junk.Keep = name => DropWatchList.Matches(watched, name);
        _game.Junk.AutoReject = s.AutoRejectJunk;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (GameEvent e in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Handle(e);
                }
                catch (Exception ex)
                {
                    _log($"Notification handling failed: {ex.GetType().Name}");
                }
                Interlocked.Increment(ref _processed);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(GameEvent e)
    {
        NotificationSettings s = Settings.Current;
        bool watched = e.Kind is GameEventKind.ItemDropped or GameEventKind.ItemObtained && DropWatchList.Matches(s.WatchedItems, e.Name);
        if (NotificationEntry.From(e) is NotificationEntry entry)
            History.Add(entry with { Watched = watched });

        switch (e.Kind)
        {
            case GameEventKind.ItemDropped or GameEventKind.ItemObtained when watched:
                OnWatchedItem(e, s);
                break;
            case GameEventKind.ScriptStarted:
                _scriptStartedAt = e.At;
                _runningScript = e.Name;
                _startGold = Interlocked.Read(ref _gold);
                if (s.WebhookEnabled && s.WebhookNotifyStarted)
                    Sender.Enqueue(WebhookPayloads.ScriptStarted(WebhookName, e.Name));
                break;
            case GameEventKind.ScriptStopped:
            {
                string? script = e.Name ?? _runningScript;
                if (s.WebhookEnabled && s.WebhookNotifyStopped && _scriptStartedAt is DateTimeOffset started)
                {
                    long gold = Interlocked.Read(ref _gold);
                    long? earned = _startGold != long.MinValue && gold != long.MinValue ? gold - _startGold : null;
                    Sender.Enqueue(WebhookPayloads.SessionConcluded(WebhookName, script, e.At - started, earned, _counters, s.WebhookIncludeMap ? _map : null));
                }
                _scriptStartedAt = null;
                _runningScript = null;
                break;
            }
            case GameEventKind.ScriptError:
                if (s.WebhookEnabled && s.WebhookNotifyCrashed)
                    Sender.Enqueue(WebhookPayloads.ScriptError(WebhookName, e.Name ?? _runningScript));
                break;
            case GameEventKind.Relogin:
                if (s.WebhookEnabled && s.WebhookNotifyRelogged)
                    Sender.Enqueue(WebhookPayloads.Relogin(WebhookName, e.Flag));
                break;
        }
    }

    private void OnWatchedItem(GameEvent e, NotificationSettings s)
    {
        string name = e.Name ?? string.Empty;
        bool obtained = e.Kind == GameEventKind.ItemObtained;
        if (obtained ? s.NotifyWatchedObtained : s.NotifyWatchedDrops)
        {
            try
            {
                _alerts.Post(new DropAlert(e.Id, name, Math.Max(1, e.Quantity), obtained, s.ShowNamesOnLockScreen));
            }
            catch (Exception ex)
            {
                _log($"Drop notification failed: {ex.GetType().Name}");
            }
        }
        if (s.WebhookEnabled && s.WebhookNotifyItemDrops)
        {
            // An item farmed each kill would otherwise post a card per kill.
            int key = obtained ? ~e.Id : e.Id;
            if (_lastWebhookDrop.TryGetValue(key, out DateTimeOffset last) && e.At - last < WebhookDropCooldown)
                return;
            _lastWebhookDrop[key] = e.At;
            Sender.Enqueue(WebhookPayloads.DropAlert(WebhookName, name, e.Quantity, obtained, s.WebhookIncludeMap ? _map : null));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Settings.Changed -= OnSettingsChanged;
        _engine.SnapshotChanged -= OnSnapshot;
        if (_game is not null)
            _game.Raised -= OnGameEvent;
        _events.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        History.Dispose();
        await Sender.DisposeAsync().ConfigureAwait(false);
    }
}
