using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
using Skua.Core.Headless;

namespace Skua.App.Droid.Tests;

/// <summary>The UI while the app is in the background: nothing is pushed into views, the backlog stays bounded.</summary>
public class BackgroundUiTests
{
    [Fact]
    public void BoundedFeedDropsTheOldestAndSaysSo()
    {
        BoundedFeed<int> feed = new(3);
        feed.Add(1);
        feed.Add(2);
        (IReadOnlyList<int> items, bool overflowed) = feed.Drain();
        Assert.Equal(new[] { 1, 2 }, items);
        Assert.False(overflowed);

        for (int i = 0; i < 10; i++)
            feed.Add(i);
        Assert.Equal(3, feed.Count);
        (items, overflowed) = feed.Drain();
        Assert.Equal(new[] { 7, 8, 9 }, items);
        Assert.True(overflowed);
        Assert.Equal(0, feed.Count);
        Assert.False(feed.Drain().Overflowed); // reset by the drain
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedFeed<int>(0));
    }

    [Fact]
    public async Task HiddenConsoleQueuesNothingIntoTheListAndCatchesUpWhenShown()
    {
        await using Rig rig = new();
        ConsoleViewModel console = rig.Shell.Console;
        rig.RunPosted();
        int before = console.LogLines.Count;

        rig.Shell.SetUiVisible(false);
        for (int i = 0; i < 50; i++)
            rig.Scripts.Emit($"hidden line {i}");
        Assert.Equal(0, rig.PostedCount); // no UI work while hidden
        Assert.Equal(before, console.LogLines.Count);

        rig.Shell.SetUiVisible(true);
        rig.RunPosted();
        List<string> shown = console.LogLines.Select(l => l.Message).Where(m => m.StartsWith("hidden line", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"hidden line {i}"), shown);

        // Visible again: lines flow as before.
        rig.Scripts.Emit("visible line");
        rig.RunPosted();
        Assert.Equal("visible line", console.LogLines[^1].Message);
    }

    [Fact]
    public async Task ALongBackgroundRunRebuildsTheListOnceWithTheNewestLines()
    {
        await using Rig rig = new();
        ConsoleViewModel console = rig.Shell.Console;
        rig.RunPosted();
        rig.Shell.SetUiVisible(false);
        int total = ConsoleViewModel.PendingCapacity * 3;
        for (int i = 0; i < total; i++)
            rig.Scripts.Emit($"run line {i}");
        Assert.Equal(0, rig.PostedCount);

        rig.Shell.SetUiVisible(true);
        rig.RunPosted();
        Assert.Equal(ConsoleViewModel.VisibleLines, console.LogLines.Count);
        Assert.Equal($"run line {total - 1}", console.LogLines[^1].Message);
        Assert.Equal(console.LogLines.Count, console.LogLines.Distinct().Count());

        // The lines that were queued as well as rebuilt are not shown twice.
        rig.Scripts.Emit("after");
        rig.RunPosted();
        Assert.Equal(console.LogLines.Count, console.LogLines.Select(l => l.Message).Distinct().Count());
        Assert.Equal("after", console.LogLines[^1].Message);
    }

    [Fact]
    public async Task SnapshotsWaitWhileHiddenAndTheLatestIsAppliedOnShow()
    {
        await using Rig rig = new();
        rig.RunPosted();
        rig.Shell.SetUiVisible(false);
        await rig.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => rig.Engine.Snapshot.IsOnline);
        await Task.Delay(100); // a few publish intervals
        rig.RunPosted();
        Assert.False(rig.Shell.IsOnline); // nothing was applied while hidden

        rig.Shell.SetUiVisible(true);
        rig.RunPosted();
        Assert.True(rig.Shell.IsOnline);
    }

    /// <summary>A queue dispatcher (the test runs what was posted) and a script host that only logs.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Action> _posted = new();

        public Rig()
        {
            AppPaths paths = AppPaths.UnderRoot(Sandbox.NewDir());
            paths.EnsureCreated();
            Engine = Aqw.NewEngine(Scripts);
            Host = new EngineHost(Engine, new SettingsStore(paths.SettingsFile), new CredentialVault(new MemorySecretStore()), new ResumeStore(paths.ResumeFile), new RecordingPlatform());
            ScriptLibrary library = new(paths.ScriptsDir);
            Shell = new ShellViewModel(new AppServices(Host, new QueueDispatcher(_posted), new NoPicker(), library, new ScriptRepositorySync(library, paths.SkuaDir, Aqw.Http), paths));
        }

        public FakeAqw Aqw { get; } = new();

        public LogScriptHost Scripts { get; } = new();

        public HeadlessBotEngine Engine { get; }

        public EngineHost Host { get; }

        public ShellViewModel Shell { get; }

        public int PostedCount
        {
            get
            {
                lock (_posted)
                    return _posted.Count;
            }
        }

        public void RunPosted()
        {
            while (true)
            {
                Action[] batch;
                lock (_posted)
                {
                    batch = _posted.ToArray();
                    _posted.Clear();
                }
                if (batch.Length == 0)
                    return;
                foreach (Action a in batch)
                    a();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Shell.Dispose();
            await Host.DisposeAsync();
            await Aqw.DisposeAsync();
        }
    }

    private sealed class QueueDispatcher(List<Action> posted) : IUiDispatcher
    {
        public void Post(Action action)
        {
            lock (posted)
                posted.Add(action);
        }
    }

    private sealed class LogScriptHost : IScriptHost
    {
        public ScriptSupport Support { get; } = new(false, "test");

        public ScriptStatus Status => ScriptStatus.Idle;

        public int LoadedAssemblyCount => 0;

        public event EventHandler<ScriptStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<LogEntry>? Log;

        public void Emit(string message) => Log?.Invoke(this, new LogEntry(DateTimeOffset.UtcNow, EngineLogLevel.Info, "script", message));

        public void Attach(AqwSession session)
        {
        }

        public void Load(string scriptPath)
        {
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
