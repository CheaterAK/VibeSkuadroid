using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
using Skua.Core.Headless;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Skua.App.Droid.Tests;

internal static class Sandbox
{
    /// <summary>Skua.Core captures ApplicationData once; point it at a private folder before any Skua type loads.</summary>
    public static readonly string Root = Path.Combine(Path.GetTempPath(), $"skua-droid-tests-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, "xdg"));
    }

    public static string NewDir()
    {
        string dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task Until(Func<bool> condition, int timeoutMs = 8000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, byte[]> Entries { get; } = new();

    public bool IsAvailable { get; set; } = true;

    /// <summary>Simulates a Keystore that fails transiently: Load answers Unavailable and keeps the entry.</summary>
    public bool Transient { get; set; }

    /// <summary>How many times an entry was decrypted (Load called).</summary>
    public int Loads { get; private set; }

    /// <summary>Simulates a Keystore that refuses to encrypt.</summary>
    public bool FailSaves { get; set; }

    public void Save(string name, ReadOnlySpan<byte> plaintext)
    {
        if (FailSaves)
            throw new InvalidOperationException("Keystore refused to encrypt.");
        Entries[name] = plaintext.ToArray();
    }

    public SecretLookup Load(string name)
    {
        Loads++;
        if (!Entries.TryGetValue(name, out byte[]? v))
            return SecretLookup.NotFound;
        return Transient ? SecretLookup.Unavailable : SecretLookup.Found(v.ToArray());
    }

    public bool Contains(string name) => Entries.ContainsKey(name);

    public void Delete(string name) => Entries.Remove(name);
}

internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath.Replace("/game/", string.Empty, StringComparison.Ordinal);
        lock (Requests)
            Requests.Add(path);
        return Task.FromResult(Routes.TryGetValue(path, out var route)
            ? route(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
}

internal sealed class RecordingPlatform : IPlatformServices
{
    public string DeviceDescription => "test device";

    public string AppVersion => "0.0-test";

    public bool NotificationsAllowed { get; set; } = true;

    public void RequestNotificationPermission() => NotificationRequests++;

    public int NotificationRequests { get; private set; }

    public bool IgnoringBatteryOptimizations { get; set; } = true;

    public void RequestIgnoreBatteryOptimizations()
    {
    }

    public bool CanRestartProcess { get; set; } = true;

    public List<(string Reason, bool Resume)> Restarts { get; } = new();

    public void RestartProcess(string reason, bool resumeLogin) => Restarts.Add((reason, resumeLogin));

    public string? Shared { get; private set; }

    public void ShareText(string subject, string text) => Shared = text;

    public bool EngineServiceRunning => false;
}

internal sealed class NoPicker : IFilePicker
{
    public string? Copied { get; private set; }

    public Task<IReadOnlyList<PickedFile>> PickFilesAsync() => Task.FromResult<IReadOnlyList<PickedFile>>([]);

    public Task<IReadOnlyList<PickedFile>> PickFolderAsync() => Task.FromResult<IReadOnlyList<PickedFile>>([]);

    public Task CopyTextAsync(string text)
    {
        Copied = text;
        return Task.CompletedTask;
    }
}

/// <summary>A fake AQW world: web API (login, servers) over <see cref="FakeHttp"/> and the game socket over <see cref="FakeAqwServer"/>.</summary>
internal sealed class FakeAqw : IAsyncDisposable
{
    public const string Password = "hunter2-secret";

    public FakeAqw()
    {
        Http.Routes["api/login/now"] = request =>
        {
            Interlocked.Increment(ref _logins);
            string body = request.Content!.ReadAsStringAsync().Result;
            bool ok = body.Contains("pass=" + Uri.EscapeDataString(Password), StringComparison.Ordinal);
            JsonObject login = ok
                ? new JsonObject { ["bSuccess"] = 1, ["sToken"] = FakeAqwServer.Token, ["unm"] = "Hero", ["userid"] = 1, ["iLevel"] = 30, ["iUpgDays"] = -1 }
                : new JsonObject { ["bSuccess"] = 0, ["sMsg"] = "Wrong password" };
            return FakeHttp.Json(new JsonObject { ["login"] = login, ["servers"] = Servers() }.ToJsonString());
        };
        Http.Routes["api/data/servers"] = _ => FakeHttp.Json(Servers().ToJsonString());
        Http.Routes["api/data/gameversion"] = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("no") };
    }

    private int _logins;

    public FakeAqwServer Server { get; } = new();

    public FakeHttp Http { get; } = new();

    public int Logins => _logins;

    public AqwSession NewSession()
    {
        AqwSessionOptions options = new()
        {
            DiscoverClientVersion = false,
            Connection = new AqwConnectionOptions
            {
                SendGap = TimeSpan.FromMilliseconds(15),
                SlowSendGap = TimeSpan.FromMilliseconds(60),
                LoginTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                LivenessCheckInterval = TimeSpan.FromMilliseconds(50),
                AwaitReplyTimeout = TimeSpan.FromMilliseconds(400),
                ProbeTimeout = TimeSpan.FromMilliseconds(400),
                IdleProbeAfter = TimeSpan.Zero,
            },
        };
        return new AqwSession(options, new AqwWebApi(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, Http));
    }

    public HeadlessBotEngine NewEngine(IScriptHost? scripts = null, bool autoReconnect = true) => new(new HeadlessEngineOptions
    {
        SessionFactory = NewSession,
        AutoReconnect = autoReconnect,
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        MaxReconnectAttempts = 5,
        PublishInterval = TimeSpan.FromMilliseconds(20),
        MemorySampleInterval = TimeSpan.FromMilliseconds(100),
    }, scripts);

    private JsonArray Servers() => new(
        new JsonObject { ["sName"] = "Sandbox", ["sIP"] = "127.0.0.1", ["iPort"] = Server.Port, ["iCount"] = 3, ["iMax"] = 1000, ["bOnline"] = 1, ["bUpg"] = 0 },
        new JsonObject { ["sName"] = "Gold", ["sIP"] = "127.0.0.1", ["iPort"] = 1, ["iCount"] = 0, ["iMax"] = 1000, ["bOnline"] = 1, ["bUpg"] = 1 });

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

/// <summary>Everything a ViewModel needs, on a fake AQW world.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public TestApp(bool vaultAvailable = true)
    {
        Paths = AppPaths.UnderRoot(Sandbox.NewDir());
        Paths.EnsureCreated();
        Store = new MemorySecretStore { IsAvailable = vaultAvailable };
        Settings = new SettingsStore(Paths.SettingsFile);
        Engine = Aqw.NewEngine();
        Host = new EngineHost(Engine, Settings, new CredentialVault(Store), new ResumeStore(Paths.ResumeFile), Platform);
        Services = new AppServices(Host, new InlineDispatcher(), Picker, new ScriptLibrary(Paths.ScriptsDir), new PendingScriptRepositorySync(), Paths);
    }

    public FakeAqw Aqw { get; } = new();

    public AppPaths Paths { get; }

    public MemorySecretStore Store { get; }

    public SettingsStore Settings { get; }

    public HeadlessBotEngine Engine { get; }

    public EngineHost Host { get; }

    public RecordingPlatform Platform { get; } = new();

    public NoPicker Picker { get; } = new();

    public AppServices Services { get; }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Aqw.DisposeAsync();
    }
}
