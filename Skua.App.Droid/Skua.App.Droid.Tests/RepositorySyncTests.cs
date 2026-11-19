using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.ViewModels;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.App.Droid.Tests;

/// <summary>A fake raw.githubusercontent.com: the auqw/Scripts index, the scripts and the data files. No network.</summary>
internal sealed class FakeRepository : HttpMessageHandler
{
    public const string Root = ScriptRepositorySync.BranchRoot;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Index entries that are not real files (hostile paths and URLs).</summary>
    public List<JsonObject> ExtraEntries { get; } = new();

    public List<Uri> Requests { get; } = new();

    /// <summary>Answers for a path instead of the file (status codes, rate limits).</summary>
    public Func<string, HttpResponseMessage?>? Override { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public void Add(string path, string content) => Files[path] = Encoding.UTF8.GetBytes(content);

    public string IndexJson()
    {
        JsonArray index = new();
        foreach ((string path, byte[] bytes) in Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal)))
        {
            index.Add(new JsonObject
            {
                ["name"] = "null",
                ["path"] = path,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ["fileName"] = Path.GetFileName(path),
                ["downloadUrl"] = "https://raw.githubusercontent.com/auqw/Scripts/Skua/" + path,
            });
        }
        foreach (JsonObject extra in ExtraEntries)
            index.Add(extra.DeepClone());
        return index.ToJsonString();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        lock (Requests)
            Requests.Add(uri);
        if (Gate is TaskCompletionSource gate)
            await gate.Task.WaitAsync(cancellationToken);
        string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        foreach (string prefix in new[] { "auqw/Scripts/refs/heads/Skua/", "auqw/Scripts/Skua/" })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                path = path[prefix.Length..];
        }
        if (Override?.Invoke(path) is HttpResponseMessage custom)
            return custom;
        if (path == "scripts.json")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IndexJson()) };
        return Files.TryGetValue(path, out byte[]? bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public class RepositorySyncTests
{
    private static (ScriptLibrary Library, string SkuaDir, FakeRepository Repo, ScriptRepositorySync Sync) Setup()
    {
        string root = Sandbox.NewDir();
        string skua = Path.Combine(root, "VibeSkua");
        ScriptLibrary library = new(Path.Combine(skua, "Scripts"));
        FakeRepository repo = new();
        repo.Add("CoreBots.cs", "public class CoreBots {}");
        repo.Add("CoreFarms.cs", "//cs_include Scripts/CoreBots.cs\npublic class CoreFarms {}");
        repo.Add("Story/DoomVault.cs", "//cs_include Scripts/CoreBots.cs\n//cs_include Scripts/CoreFarms.cs\npublic class DoomVault {}");
        repo.Add("Other/Deep/Nested/Thing.cs", "public class Thing {}");
        repo.Add("Skills/AdvancedSkills.json", "{\"Generic\":[]}");
        repo.Add("QuestData.json", "[{\"ID\":1,\"Name\":\"One\"},{\"ID\":2,\"Name\":\"Two\"}]");
        repo.Add("JunkItems.json", "[\"Junk\"]");
        return (library, skua, repo, new ScriptRepositorySync(library, skua, repo, parallelism: 3, retryDelays: [TimeSpan.FromMilliseconds(10)]));
    }

    [Fact]
    public async Task SyncDownloadsTheTreeAndRefusesWhatImportWouldRefuse()
    {
        (ScriptLibrary library, string skua, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "../escape.cs", ["size"] = 1, ["sha256"] = "00" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "/etc/abs.cs", ["size"] = 1, ["sha256"] = "00" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Cached-Scripts/1-CoreBots.cs", ["size"] = 1, ["sha256"] = "00" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Tools/run.exe", ["size"] = 1, ["sha256"] = "00" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Evil/host.cs", ["size"] = 1, ["sha256"] = "00", ["downloadUrl"] = "https://evil.example/x.cs" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Evil/plain.cs", ["size"] = 1, ["sha256"] = "00", ["downloadUrl"] = "http://raw.githubusercontent.com/auqw/Scripts/Skua/x.cs" });
        Directory.CreateDirectory(library.Root);
        File.WriteAllText(Path.Combine(library.Root, "MyOwn.cs"), "mine");
        File.WriteAllText(Path.Combine(skua, "QuestData.json"), "[{\"ID\":2,\"Name\":\"Old two\"},{\"ID\":99,\"Name\":\"Seen in game\"}]");
        List<RepositorySyncProgress> progress = new();

        RepositorySyncResult result = await sync.SyncAsync(new SyncProgressList(progress), CancellationToken.None);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(4, result.Listed);
        Assert.Equal(4, result.Downloaded);
        Assert.Equal(6, result.Refused);
        Assert.Equal(3, result.DataFilesUpdated);
        Assert.Equal("public class CoreBots {}", File.ReadAllText(Path.Combine(library.Root, "CoreBots.cs")));
        Assert.True(File.Exists(Path.Combine(library.Root, "Story", "DoomVault.cs")));
        Assert.True(File.Exists(Path.Combine(library.Root, "Other", "Deep", "Nested", "Thing.cs")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(library.Root, "MyOwn.cs"))); // never touched
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(library.Root)!, "escape.cs")));
        Assert.False(Directory.Exists(Path.Combine(library.Root, "Cached-Scripts")));
        Assert.False(Directory.Exists(Path.Combine(library.Root, "Tools")));
        Assert.False(Directory.Exists(Path.Combine(library.Root, "Evil")));
        Assert.Empty(Directory.EnumerateFiles(library.Root, "*.sync-tmp", SearchOption.AllDirectories));
        Assert.Equal("{\"Generic\":[]}", File.ReadAllText(Path.Combine(skua, "AdvancedSkills.json")));
        Assert.Equal("[\"Junk\"]", File.ReadAllText(Path.Combine(library.Root, "JunkItems.json")));
        JsonArray quests = JsonNode.Parse(File.ReadAllText(Path.Combine(skua, "QuestData.json")))!.AsArray();
        Assert.Equal([1, 2, 99], quests.Select(q => (int)q!["ID"]!));
        Assert.Equal("Two", (string)quests[1]!["Name"]!); // the repository wins for quests it has
        Assert.True(File.Exists(Path.Combine(skua, "scripts_cache.json")));
        Assert.Contains("4 scripts: 4 downloaded", result.Summary);
        Assert.Equal("done", progress[^1].Stage);
        Assert.Contains(progress, p => p.Stage == "downloading" && p.Done > 0);
        // Only raw.githubusercontent.com over HTTPS, nothing else.
        Assert.All(repo.Requests, u => Assert.True(u.Scheme == "https" && u.Host == "raw.githubusercontent.com", u.ToString()));

        // The library browses the synced tree and the include path resolves where Skua looks (Scripts/ = the root).
        Assert.Contains(library.List(), e => e.IsFolder && e.Name == "Story");
        Assert.Contains(library.Search("Doom"), e => e.RelativePath == "Story/DoomVault.cs");
    }

    [Fact]
    public async Task ASecondSyncDownloadsOnlyWhatChanged()
    {
        (ScriptLibrary library, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        Assert.True((await sync.SyncAsync(null, CancellationToken.None)).Succeeded);
        repo.Requests.Clear();

        RepositorySyncResult again = await sync.SyncAsync(null, CancellationToken.None);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(4, again.UpToDate);
        Assert.Equal(0, again.DataFilesUpdated);
        Assert.DoesNotContain(repo.Requests, u => u.AbsolutePath.EndsWith(".cs", StringComparison.Ordinal));

        repo.Add("CoreBots.cs", "public class CoreBots { public int V = 2; }");
        File.WriteAllText(Path.Combine(library.Root, "Story", "DoomVault.cs"), "edited locally");
        repo.Requests.Clear();
        RepositorySyncResult third = await sync.SyncAsync(null, CancellationToken.None);
        Assert.Equal(2, third.Downloaded);
        Assert.Equal(["/auqw/Scripts/Skua/CoreBots.cs", "/auqw/Scripts/Skua/Story/DoomVault.cs"],
            repo.Requests.Where(u => u.AbsolutePath.EndsWith(".cs", StringComparison.Ordinal)).Select(u => u.AbsolutePath).Order());
        Assert.Contains("V = 2", File.ReadAllText(Path.Combine(library.Root, "CoreBots.cs")));
    }

    [Fact]
    public async Task ARateLimitStopsTheSyncKeepsWhatWasDoneAndTheNextSyncResumes()
    {
        (ScriptLibrary library, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        bool limited = true;
        repo.Override = path =>
        {
            if (!limited || path != "Story/DoomVault.cs")
                return null;
            HttpResponseMessage r = new(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return r;
        };
        RepositorySyncResult first = await sync.SyncAsync(null, CancellationToken.None);
        Assert.True(first.RateLimited);
        Assert.Equal(TimeSpan.FromMinutes(7), first.RetryAfter);
        Assert.False(first.Succeeded);
        Assert.Contains("Sync again in about 7 min", first.Summary);
        Assert.False(File.Exists(Path.Combine(library.Root, "Story", "DoomVault.cs")));
        Assert.Equal(1, repo.Requests.Count(u => u.AbsolutePath.EndsWith("DoomVault.cs", StringComparison.Ordinal))); // no retry hammering

        limited = false;
        RepositorySyncResult second = await sync.SyncAsync(null, CancellationToken.None);
        Assert.True(second.Succeeded, second.Summary);
        Assert.True(File.Exists(Path.Combine(library.Root, "Story", "DoomVault.cs")));
        Assert.Equal(4, second.Downloaded + second.UpToDate);
        Assert.True(second.Downloaded >= 1);
    }

    [Fact]
    public async Task ServerErrorsAreRetriedThenCountedAsFailures()
    {
        (_, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        int calls = 0;
        repo.Override = path => path == "CoreFarms.cs" && Interlocked.Increment(ref calls) <= 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
        RepositorySyncResult ok = await sync.SyncAsync(null, CancellationToken.None);
        Assert.True(ok.Succeeded, ok.Summary); // one retry was enough

        repo.Add("CoreFarms.cs", "changed");
        repo.Override = path => path == "CoreFarms.cs" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        RepositorySyncResult failed = await sync.SyncAsync(null, CancellationToken.None);
        Assert.Equal(1, failed.Failed);
        Assert.Contains(failed.Problems, p => p.Contains("CoreFarms.cs") && p.Contains("500"));
    }

    [Fact]
    public async Task CancelEndsTheSyncWithACancelledResult()
    {
        (_, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        repo.Gate = new TaskCompletionSource();
        using CancellationTokenSource cts = new();
        Task<RepositorySyncResult> running = sync.SyncAsync(null, cts.Token);
        await Sandbox.Until(() => repo.Requests.Count > 0);
        Assert.True(sync.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync(null, CancellationToken.None));
        cts.Cancel();
        RepositorySyncResult result = await running;
        Assert.True(result.Cancelled);
        Assert.False(sync.IsRunning);
    }

    [Fact]
    public async Task TheScriptsPageSyncsAsBackgroundWorkAndShowsTheCount()
    {
        await using TestApp app = new();
        (_, _, FakeRepository repo, _) = Setup();
        ScriptRepositorySync sync = new(app.Services.Library, app.Paths.SkuaDir, repo);
        ScriptsViewModel vm = new(app.Services with { RepositorySync = sync });
        repo.Gate = new TaskCompletionSource();
        Task syncing = vm.SyncRepositoryCommand.ExecuteAsync(null);
        await Sandbox.Until(() => app.Engine.Snapshot.BackgroundWork is not null);
        Assert.True(app.Engine.Snapshot.NeedsForegroundService); // keeps the foreground service (and the process) up
        Assert.True(vm.IsSyncing);
        Assert.True(vm.CancelSyncCommand.CanExecute(null));
        repo.Gate.SetResult();
        await syncing;
        Assert.False(vm.IsSyncing);
        Assert.Contains("Synced. 4 scripts: 4 downloaded", vm.Message);
        await Sandbox.Until(() => app.Engine.Snapshot.BackgroundWork is null);
        Assert.False(app.Engine.Snapshot.NeedsForegroundService);
        Assert.Contains(vm.Entries, e => e.Name == "Story");
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/Skua/CoreBots.cs", true)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/refs/heads/Skua/Story/Doom Vault.cs", true)]
    [InlineData("https://raw.githubusercontent.com/someone/Scripts/Skua/CoreBots.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/ScriptsEvil/Skua/CoreBots.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/../../evil/repo/x.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/Skua/%2e%2e/%2e%2e/evil/x.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/Skua/a%5c..%5cx.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/Skua/x.cs?redirect=1", false)]
    [InlineData("https://user@raw.githubusercontent.com/auqw/Scripts/Skua/x.cs", false)]
    [InlineData("https://raw.githubusercontent.com:8443/auqw/Scripts/Skua/x.cs", false)]
    [InlineData("http://raw.githubusercontent.com/auqw/Scripts/Skua/x.cs", false)]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/", false)]
    public void DownloadUrlsMustBeInTheScriptsRepository(string url, bool allowed)
    {
        Assert.Equal(allowed, ScriptRepositorySync.IsAllowed(new Uri(url)));
        // The fallback for an entry without a URL is allowed too.
        Assert.True(ScriptRepositorySync.IsAllowed(new Uri(ScriptRepositorySync.BranchRoot + "CoreBots.cs")));
    }

    [Fact]
    public async Task EntriesFromOtherRepositoriesOrWithoutAHashAreRefused()
    {
        (ScriptLibrary library, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        repo.ExtraEntries.Add(new JsonObject
        {
            ["path"] = "Evil/other.cs", ["size"] = 4, ["sha256"] = Hex("evil"),
            ["downloadUrl"] = "https://raw.githubusercontent.com/someone/Scripts/Skua/other.cs",
        });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Evil/nohash.cs", ["size"] = 4, ["downloadUrl"] = "https://raw.githubusercontent.com/auqw/Scripts/Skua/Evil/nohash.cs" });
        repo.ExtraEntries.Add(new JsonObject { ["path"] = "Evil/badhash.cs", ["size"] = 4, ["sha256"] = "zz", ["downloadUrl"] = "https://raw.githubusercontent.com/auqw/Scripts/Skua/Evil/badhash.cs" });

        RepositorySyncResult result = await sync.SyncAsync(null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(3, result.Refused);
        Assert.Equal(4, result.Downloaded);
        Assert.False(Directory.Exists(Path.Combine(library.Root, "Evil")));
        Assert.DoesNotContain(repo.Requests, u => u.AbsolutePath.Contains("/someone/") || u.AbsolutePath.Contains("/Evil/"));
        Assert.Contains(result.Problems, p => p.Contains("Evil/other.cs") && p.Contains(ScriptRepositorySync.AllowedDownloadPrefix));
        Assert.Contains(result.Problems, p => p.Contains("Evil/nohash.cs") && p.Contains("SHA-256"));
    }

    [Fact]
    public async Task DownloadsThatDoNotMatchTheIndexAreNotWritten()
    {
        string root = Sandbox.NewDir();
        string skua = Path.Combine(root, "VibeSkua");
        ScriptLibrary library = new(Path.Combine(skua, "Scripts"));
        FakeRepository repo = new();
        repo.Add("CoreBots.cs", "public class CoreBots {}");
        repo.Add("CoreFarms.cs", "public class CoreFarms {}");
        repo.Add("CoreStory.cs", "public class CoreStory {}");
        List<string> warnings = new();
        using ScriptRepositorySync sync = new(library, skua, repo, parallelism: 2, retryDelays: [], warn: w => { lock (warnings) warnings.Add(w); });
        // Same length, other content; and a longer body than listed.
        repo.Override = path => path switch
        {
            "CoreFarms.cs" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("public class CoreEvil0 {}") },
            "CoreStory.cs" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("public class CoreStory {} // and more") },
            _ => null,
        };
        Assert.Equal("public class CoreFarms {}".Length, "public class CoreEvil0 {}".Length);

        RepositorySyncResult result = await sync.SyncAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(2, result.Failed);
        Assert.True(File.Exists(Path.Combine(library.Root, "CoreBots.cs")));
        Assert.False(File.Exists(Path.Combine(library.Root, "CoreFarms.cs")));
        Assert.False(File.Exists(Path.Combine(library.Root, "CoreStory.cs")));
        Assert.Empty(Directory.EnumerateFiles(library.Root, "*.sync-tmp", SearchOption.AllDirectories));
        Assert.Contains(result.Problems, p => p.Contains("CoreFarms.cs") && p.Contains("SHA-256"));
        Assert.Contains(result.Problems, p => p.Contains("CoreStory.cs"));
        Assert.Contains(warnings, w => w.Contains("CoreFarms.cs") && w.Contains("skipped"));

        // A tampered local copy is not "up to date" either: the next sync replaces it.
        repo.Override = null;
        File.WriteAllText(Path.Combine(library.Root, "CoreBots.cs"), "public class CoreEvil {}");
        RepositorySyncResult again = await sync.SyncAsync(null, CancellationToken.None);
        Assert.True(again.Succeeded, again.Summary);
        Assert.Equal("public class CoreBots {}", File.ReadAllText(Path.Combine(library.Root, "CoreBots.cs")));
    }

    [Fact]
    public async Task AnIndexWithTooManyEntriesFailsTheSyncBeforeAnythingIsWritten()
    {
        (ScriptLibrary library, string skua, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        JsonArray big = new();
        for (int i = 0; i <= ScriptRepositorySync.MaxEntries; i++)
            big.Add(new JsonObject { ["path"] = $"Many/S{i}.cs", ["size"] = 4, ["sha256"] = Hex("x"), ["downloadUrl"] = $"https://raw.githubusercontent.com/auqw/Scripts/Skua/Many/S{i}.cs" });
        string json = big.ToJsonString();
        repo.Override = path => path == "scripts.json" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) } : null;

        RepositorySyncResult result = await sync.SyncAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Listed);
        Assert.StartsWith("Sync failed:", result.Summary);
        Assert.Contains("entries", result.Summary);
        Assert.Single(repo.Requests); // the index only
        Assert.False(Directory.Exists(library.Root) && Directory.EnumerateFileSystemEntries(library.Root).Any());
        Assert.False(File.Exists(Path.Combine(skua, "scripts_cache.json")));
    }

    [Fact]
    public async Task AnIndexListingTooManyBytesFailsTheSyncBeforeAnythingIsDownloaded()
    {
        (ScriptLibrary library, _, FakeRepository repo, ScriptRepositorySync sync) = Setup();
        int count = (int)(ScriptRepositorySync.MaxTotalBytes / ScriptLibrary.MaxFileBytes) + 1;
        for (int i = 0; i < count; i++)
            repo.ExtraEntries.Add(new JsonObject { ["path"] = $"Big/B{i}.cs", ["size"] = ScriptLibrary.MaxFileBytes, ["sha256"] = Hex("x"), ["downloadUrl"] = $"https://raw.githubusercontent.com/auqw/Scripts/Skua/Big/B{i}.cs" });

        RepositorySyncResult result = await sync.SyncAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Listed);
        Assert.Contains("MB allowed", result.Summary);
        Assert.Single(repo.Requests);
        Assert.False(File.Exists(Path.Combine(library.Root, "CoreBots.cs")));
    }

    private static string Hex(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    [Fact]
    public void CoreBotsKeysComeFromTheCoreSourcesAndSavingKeepsUnknownLines()
    {
        string root = Sandbox.NewDir();
        string scripts = Path.Combine(root, "Scripts");
        string options = Path.Combine(root, "options");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(options);
        File.WriteAllText(Path.Combine(scripts, "CoreBots.cs"),
            "if (CBOBool(\"PrivateRooms\", out bool a)) {} if (CBOInt(\"PrivateRoomNr\", out int n)) {} CBOString(\"SoloClassSelect\", out string s); CBOBool(\"PrivateRooms\", out a);");
        File.WriteAllText(Path.Combine(scripts, "CoreFarms.cs"), "CBOString(\"FarmClassSelect\", out string f);");
        File.WriteAllText(Path.Combine(scripts, "NotCore.cs"), "CBOBool(\"Ignored\", out bool i);");
        File.WriteAllLines(Path.Combine(options, "CBO_Storage(Hero).txt"), ["PrivateRooms: True", "Weapon1Select: Sword", "SoloClassSelect: Old"]);

        ScriptOptionSet set = CoreBotsOptions.Load(scripts, options, "hero"); // the existing file matches ignoring case
        Assert.Equal("CBO_Storage(Hero)", set.Storage);
        Assert.Equal(["PrivateRooms", "PrivateRoomNr", "SoloClassSelect", "FarmClassSelect"], set.Items.Select(i => i.Name));
        Assert.Equal([ScriptOptionKind.Toggle, ScriptOptionKind.Number, ScriptOptionKind.Text, ScriptOptionKind.Text], set.Items.Select(i => i.Kind));
        Assert.Equal(["True", "", "Old", ""], set.Items.Select(i => i.Value));
        Assert.Equal("Private Room Nr", set.Items[1].Title);

        Assert.Throws<EngineException>(() => CoreBotsOptions.Save(set with { Items = set.Items.Select(i => i.Name == "PrivateRoomNr" ? i with { Value = "abc" } : i).ToList() }, options));
        CoreBotsOptions.Save(set with
        {
            Items = set.Items.Select(i => i.Name switch
            {
                "PrivateRooms" => i with { Value = "False" },
                "PrivateRoomNr" => i with { Value = "4242" },
                "SoloClassSelect" => i with { Value = "" },
                _ => i,
            }).ToList(),
        }, options);
        Assert.Equal(["PrivateRooms: False", "Weapon1Select: Sword", "PrivateRoomNr: 4242"], File.ReadAllLines(Path.Combine(options, "CBO_Storage(Hero).txt")));

        // A new account gets the lower-case name AQW reports.
        Assert.Equal(Path.Combine(options, "CBO_Storage(newbie).txt"), CoreBotsOptions.FileFor(options, "Newbie"));
    }

    [Theory]
    [InlineData("x)/../../../diagnostics/planted")]
    [InlineData("../planted")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("x).txt")]
    [InlineData("name\0")]
    [InlineData("   ")]
    [InlineData("")]
    public void CoreBotsOptionsRefuseAccountNamesThatCouldLeaveTheOptionsFolder(string account)
    {
        string root = Sandbox.NewDir();
        string scripts = Path.Combine(root, "Scripts");
        string options = Path.Combine(root, "skua", "options");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(options);
        File.WriteAllText(Path.Combine(scripts, "CoreBots.cs"), "CBOBool(\"PrivateRooms\", out bool a);");

        Assert.False(CoreBotsOptions.IsSafeAccountName(account));
        Assert.Throws<EngineException>(() => CoreBotsOptions.FileFor(options, account));
        Assert.Throws<EngineException>(() => CoreBotsOptions.Load(scripts, options, account));

        // A set naming its own storage (not from Load) is checked too.
        ScriptOptionSet forged = new(Path.Combine(scripts, "CoreBots.cs"), $"CBO_Storage({account})",
            [new ScriptOptionItem("General", "PrivateRooms", "Private Rooms", string.Empty, ScriptOptionKind.Toggle, "True", [], false)]);
        Assert.Throws<EngineException>(() => CoreBotsOptions.Save(forged, options));
        Assert.Empty(Directory.EnumerateFileSystemEntries(options));
        Assert.Empty(Directory.EnumerateFiles(root, "*planted*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("hero", "CBO_Storage(hero).txt")]
    [InlineData("My Hero_2", "CBO_Storage(my hero_2).txt")]
    [InlineData("dash-name", "CBO_Storage(dash-name).txt")]
    public void CoreBotsOptionsKeepWorkingForRealAccountNames(string account, string file)
    {
        string options = Path.Combine(Sandbox.NewDir(), "options");
        Assert.True(CoreBotsOptions.IsSafeAccountName(account));
        Assert.Equal(Path.Combine(options, file), CoreBotsOptions.FileFor(options, account));
        ScriptOptionSet set = new("CoreBots.cs", Path.GetFileNameWithoutExtension(file),
            [new ScriptOptionItem("General", "PrivateRooms", "Private Rooms", string.Empty, ScriptOptionKind.Toggle, "True", [], false)]);
        CoreBotsOptions.Save(set, options);
        Assert.Equal(["PrivateRooms: True"], File.ReadAllLines(Path.Combine(options, file)));
    }

    private sealed class SyncProgressList : IProgress<RepositorySyncProgress>
    {
        private readonly List<RepositorySyncProgress> _list;

        public SyncProgressList(List<RepositorySyncProgress> list)
        {
            _list = list;
        }

        public void Report(RepositorySyncProgress value)
        {
            lock (_list)
                _list.Add(value);
        }
    }
}
