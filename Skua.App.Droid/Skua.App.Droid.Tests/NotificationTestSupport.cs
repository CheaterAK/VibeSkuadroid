using System.Net;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Security;
using Skua.Core.Headless;

namespace Skua.App.Droid.Tests;

/// <summary>A script host without scripts that hands the engine fake game events.</summary>
internal sealed class EventScriptHost : IScriptHost, IGameEventSource
{
    private readonly UnavailableScriptHost _inner = new();

    public EventScriptHost(FakeGameEvents events)
    {
        Events = events;
    }

    public FakeGameEvents Events { get; }

    public IGameEvents? GameEvents => Events;

    public ScriptSupport Support => _inner.Support;

    public ScriptStatus Status => _inner.Status;

    public int LoadedAssemblyCount => 0;

    public event EventHandler<ScriptStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
    }

    public void Load(string scriptPath) => _inner.Load(scriptPath);

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Game events raised by the test, drop actions and a junk list recorded in memory.</summary>
internal sealed class FakeGameEvents : IGameEvents, IJunkList
{
    private readonly List<JunkItem> _junk = new();

    public event EventHandler<GameEvent>? Raised;

    public List<string> Calls { get; } = new();

    public IJunkList Junk => this;

    public void Raise(GameEvent e) => Raised?.Invoke(this, e);

    public void Drop(int id, string name, int quantity = 1) =>
        Raise(new GameEvent(GameEventKind.ItemDropped, DateTimeOffset.UtcNow) { Id = id, Name = name, Quantity = quantity });

    public void PickupDrops(IReadOnlyCollection<int> itemIds) => Record($"pickup {string.Join(",", itemIds)}");

    public void PickupAllDrops() => Record("pickup all");

    public void RejectDrops(IReadOnlyCollection<int> itemIds) => Record($"reject {string.Join(",", itemIds)}");

    public IReadOnlyList<JunkItem> Items
    {
        get
        {
            lock (_junk)
                return _junk.ToList();
        }
    }

    public bool IsJunk(int itemId)
    {
        lock (_junk)
            return _junk.Any(j => j.Id == itemId);
    }

    public void Set(IEnumerable<JunkItem> items)
    {
        lock (_junk)
        {
            _junk.Clear();
            _junk.AddRange(items);
        }
    }

    public void Reload()
    {
    }

    public int Sold;

    public int SellAll()
    {
        Record("sell");
        return Interlocked.Increment(ref Sold);
    }

    public bool AutoReject { get; set; }

    public Func<string, bool>? Keep { get; set; }

    private void Record(string call)
    {
        lock (Calls)
            Calls.Add(call);
    }
}

/// <summary>A Discord stand-in: records every request and answers with <see cref="Respond"/>.</summary>
internal sealed class FakeDiscord : HttpMessageHandler
{
    private readonly List<(Uri Uri, string Body)> _requests = new();

    public Func<int, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    /// <summary>When set, every request waits for it (a Discord that does not answer).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<(Uri Uri, string Body)> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        int n;
        lock (_requests)
        {
            _requests.Add((request.RequestUri!, body));
            n = _requests.Count;
        }
        if (Gate is TaskCompletionSource gate)
            await gate.Task.WaitAsync(cancellationToken);
        return Respond(n);
    }

    public static HttpResponseMessage TooManyRequests(string? body = null, string? retryAfterHeader = null)
    {
        HttpResponseMessage r = new((HttpStatusCode)429) { Content = new StringContent(body ?? "{}") };
        if (retryAfterHeader is not null)
            r.Headers.TryAddWithoutValidation("Retry-After", retryAfterHeader);
        return r;
    }
}

internal static class Webhooks
{
    /// <summary>A well-formed URL (nothing is ever sent to it: the tests use <see cref="FakeDiscord"/>).</summary>
    public const string Url = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD";

    public const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD";

    /// <summary>A sender whose waits are recorded and skipped.</summary>
    public static DiscordWebhookSender Sender(FakeDiscord discord, List<TimeSpan>? waits = null, int capacity = 50, int attempts = 5) =>
        new(discord, new WebhookSenderOptions
        {
            QueueCapacity = capacity,
            MaxAttempts = attempts,
            Delay = (t, ct) =>
            {
                if (waits is not null)
                {
                    lock (waits)
                        waits.Add(t);
                }
                return Task.CompletedTask;
            },
        });

    public static DiscordWebhookUrl Parse(string url = Url)
    {
        Assert.True(DiscordWebhookUrl.TryParse(url, out DiscordWebhookUrl? parsed, out string error), error);
        return parsed!;
    }

    /// <summary>A hub on <paramref name="engine"/> with a fake Discord, recorded alerts and files under <paramref name="dir"/>.</summary>
    public static NotificationHub Hub(IBotEngine engine, string dir, FakeDiscord discord, RecordingDropAlertSink alerts, MemorySecretStore? store = null, SecretScrubber? scrubber = null) =>
        new(engine,
            new NotificationSettingsStore(Path.Combine(dir, "notify.json")),
            new NotificationHistory(Path.Combine(dir, "history.json"), saveDelay: TimeSpan.FromMilliseconds(50)),
            alerts,
            new WebhookUrlVault(store ?? new MemorySecretStore()),
            Sender(discord),
            scrubber);
}
