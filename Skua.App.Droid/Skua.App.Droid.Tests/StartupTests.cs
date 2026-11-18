using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI;
using Skua.App.Droid.UI.Views;
using UiApp = Skua.App.Droid.UI.App;

namespace Skua.App.Droid.Tests;

/// <summary>Engine composition off the main thread: the gate every caller waits on, the marker, and the UI's starting state.</summary>
public class StartupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Engine
    {
        public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    }

    [Fact]
    public async Task CompositionRunsOffTheCallingThreadAndCallersDoNotWait()
    {
        using ManualResetEventSlim release = new();
        int composed = 0;
        List<(EngineStartupState State, int Thread)> seen = new();
        EngineStartup<Engine> startup = new(() =>
        {
            Interlocked.Increment(ref composed);
            Assert.True(release.Wait(Wait));
            return new Engine();
        }, (state, _) =>
        {
            lock (seen)
                seen.Add((state, Environment.CurrentManagedThreadId));
        });
        int caller = Environment.CurrentManagedThreadId;

        Task<Engine?> first = startup.EnsureStarted();
        Task<Engine?> second = startup.EnsureStarted();   // e.g. the activity while Application.OnCreate's attempt runs

        // Returned at once: composition is blocked, nobody waited for it.
        Assert.False(first.IsCompleted);
        Assert.Same(first, second);
        Assert.Equal(EngineStartupState.Starting, startup.State);
        Assert.Null(startup.Value);

        release.Set();
        Engine? engine = await first.WaitAsync(Wait);
        Assert.NotNull(engine);
        Assert.NotEqual(caller, engine!.ThreadId);
        Assert.Same(engine, startup.Value);
        Assert.Equal(EngineStartupState.Ready, startup.State);
        Assert.Equal(1, composed);

        // Ready: later callers get the same engine at once, nothing is composed again.
        Task<Engine?> later = startup.EnsureStarted();
        Assert.True(later.IsCompletedSuccessfully);
        Assert.Same(engine, await later);
        Assert.Equal(1, composed);
        lock (seen)
        {
            Assert.Equal(new[] { EngineStartupState.Starting, EngineStartupState.Ready }, seen.Select(s => s.State));
            Assert.Equal(caller, seen[0].Thread);
        }
    }

    [Fact]
    public async Task AFailureIsRecordedBeforeWaitersContinueAndTheNextCallRetries()
    {
        int attempts = 0;
        List<string> events = new();
        EngineStartup<Engine> startup = new(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("Keystore unavailable");
            return new Engine();
        }, (state, error) =>
        {
            lock (events)
                events.Add(error is null ? state.ToString() : $"{state}: {error.Message}");
        });

        Engine? failed = await startup.EnsureStarted().WaitAsync(Wait);
        Assert.Null(failed);   // never faults
        lock (events)
            Assert.Contains("Failed: Keystore unavailable", events);   // recorded before the waiter continued
        Assert.Equal(EngineStartupState.Failed, startup.State);
        Assert.IsType<IOException>(startup.Error);
        Assert.Null(startup.Value);

        Engine? retried = await startup.EnsureStarted().WaitAsync(Wait);
        Assert.NotNull(retried);
        Assert.Equal(EngineStartupState.Ready, startup.State);
        Assert.Null(startup.Error);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ObserverAndRunnerFailuresDoNotEscape()
    {
        EngineStartup<Engine> throwingObserver = new(() => new Engine(), (_, _) => throw new InvalidOperationException("diagnostics broke"));
        Assert.NotNull(await throwingObserver.EnsureStarted().WaitAsync(Wait));

        EngineStartup<Engine> noThread = new(() => new Engine(), run: _ => throw new OutOfMemoryException("no thread"));
        Assert.Null(await noThread.EnsureStarted().WaitAsync(Wait));
        Assert.Equal(EngineStartupState.Failed, noThread.State);

        EngineStartup<Engine> nothing = new(() => null!);
        Assert.Null(await nothing.EnsureStarted().WaitAsync(Wait));
        Assert.Equal(EngineStartupState.Failed, nothing.State);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneAttempt()
    {
        using ManualResetEventSlim release = new();
        int composed = 0;
        EngineStartup<Engine> startup = new(() =>
        {
            Interlocked.Increment(ref composed);
            release.Wait(Wait);
            return new Engine();
        });
        // StartNew (not Run) so the outer task is the call itself, not the composition it returns.
        Task<Task<Engine?>>[] callers = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(startup.EnsureStarted, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default))
            .ToArray();
        Task<Engine?>[] attempts = await Task.WhenAll(callers).WaitAsync(Wait);   // every call returned while composition is blocked
        Assert.All(attempts, a => Assert.False(a.IsCompleted));
        release.Set();
        Engine?[] engines = await Task.WhenAll(attempts).WaitAsync(Wait);
        Assert.Equal(1, composed);
        Assert.All(engines, e => Assert.Same(engines[0], e));
    }

    [Fact]
    public async Task WaitAsyncGivesUpAfterTheTimeoutWithoutCancellingTheAttempt()
    {
        using ManualResetEventSlim release = new();
        EngineStartup<Engine> startup = new(() =>
        {
            release.Wait(Wait);
            return new Engine();
        });
        Assert.Null(await startup.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(EngineStartupState.Starting, startup.State);
        release.Set();
        Assert.NotNull(await startup.WaitAsync(Wait));
    }

    [Fact]
    public void MarkerKeepsTheBackgroundCompositionNextToTheSteps()
    {
        ManualTime time = new();
        DiagnosticsStore store = new(Sandbox.NewDir(), time);
        DateTimeOffset started = time.GetUtcNow();

        // The UI got all the way to its first frame while the engine was still composing, then the process died.
        store.WriteMarker(4242, started, StartupStep.FirstFrame, EngineInitPhase.Started);
        StartupMarker marker = store.ReadMarker()!;
        Assert.Equal(EngineInitPhase.Started, marker.Engine);
        Assert.True(marker.DiedInEngineInit);   // the next start skips composition until the crash screen was seen
        Assert.True(marker.EndedUnexpectedly);
        Assert.False(marker.Completed);
        Assert.Contains("while the engine was still being composed", DiagnosticsStore.DescribeMarker(marker));

        // A service-only process that died while composing is a crash too; one whose composition finished is not.
        Assert.True((marker with { LastStep = StartupStep.ApplicationReady }).EndedUnexpectedly);
        Assert.False((marker with { LastStep = StartupStep.ApplicationReady, Engine = EngineInitPhase.Finished }).EndedUnexpectedly);
        Assert.False((marker with { Engine = EngineInitPhase.Finished }).DiedInEngineInit);
        Assert.True((marker with { Engine = EngineInitPhase.Finished }).Completed);

        // Markers written before the phase existed still read (as None).
        File.WriteAllText(store.MarkerPath, $"pid=7\nstarted={started:O}\nstep=3\nname=EngineInitStarted\nat={started:O}\n");
        StartupMarker old = store.ReadMarker()!;
        Assert.Equal(EngineInitPhase.None, old.Engine);
        Assert.True(old.DiedInEngineInit);
    }

    [AvaloniaFact]
    public async Task TheMainViewShowsStartingUntilTheEngineIsComposed()
    {
        await using TestApp app = new();
        Func<AppServices>? factory = AppBootstrap.Factory;
        Func<Task<bool>>? ready = AppBootstrap.EngineReady;
        int factoryCalls = 0;
        TaskCompletionSource<bool> composed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            AppBootstrap.Reset();
            AppBootstrap.Factory = () =>
            {
                factoryCalls++;
                return app.Services;
            };
            AppBootstrap.EngineReady = () => composed.Task;

            Control view = UiApp.CreateMainView();
            StartingView starting = Assert.IsType<StartingView>(view);
            Window window = new() { Width = 412, Height = 915, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(starting.IsStarting);
            Assert.Equal(0, factoryCalls);   // nothing touched the engine
            Assert.Null(AppBootstrap.ShellIfCreated);

            composed.SetResult(true);
            Assert.True(await Until(() => !starting.IsStarting));
            Assert.IsType<MainView>(starting.Content);
            Assert.NotNull(AppBootstrap.ShellIfCreated);
            Assert.Equal(1, factoryCalls);

            // Once composed, new views (another activity) are the main view directly.
            Assert.IsType<MainView>(UiApp.CreateMainView());
            window.Close();
        }
        finally
        {
            AppBootstrap.Factory = factory;
            AppBootstrap.EngineReady = ready;
            AppBootstrap.Reset();
        }
    }

    [AvaloniaFact]
    public async Task AFailedCompositionEndsInTheErrorView()
    {
        Func<AppServices>? factory = AppBootstrap.Factory;
        Func<Task<bool>>? ready = AppBootstrap.EngineReady;
        Action<string, Exception>? report = AppBootstrap.ReportError;
        List<string> reported = new();
        TaskCompletionSource<bool> composed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            AppBootstrap.Reset();
            AppBootstrap.Factory = () => throw new InvalidOperationException("The engine is not available (its composition failed or was skipped; see the crash report).");
            AppBootstrap.EngineReady = () => composed.Task;
            AppBootstrap.ReportError = (where, e) => reported.Add(where);

            StartingView starting = Assert.IsType<StartingView>(UiApp.CreateMainView());
            composed.SetResult(false);
            Assert.True(await Until(() => !starting.IsStarting));
            ScrollViewer error = Assert.IsType<ScrollViewer>(starting.Content);
            Assert.Contains("engine is not available", Assert.IsType<TextBlock>(error.Content).Text);
            Assert.Equal(new[] { "creating the main view" }, reported);

            // A start that throws is reported and does not keep the view waiting.
            AppBootstrap.EngineReady = () => throw new InvalidOperationException("no context");
            Assert.IsType<ScrollViewer>(UiApp.CreateMainView());
            Assert.Contains("starting the engine", reported);
        }
        finally
        {
            AppBootstrap.Factory = factory;
            AppBootstrap.EngineReady = ready;
            AppBootstrap.ReportError = report;
            AppBootstrap.Reset();
        }
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        return condition();
    }
}
