using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scheduler;
using Skua.Core.Headless;

namespace Skua.App.Droid.Tests;

/// <summary>How a fake script behaves when the queue starts it.</summary>
/// <param name="RunMs">How long a run lasts before it ends by itself.</param>
/// <param name="Fault">The run ends with this error (the adapter's "Type: message" form).</param>
/// <param name="StartError">The start fails with this message (compile error, ...).</param>
/// <param name="Forever">The run never ends by itself.</param>
internal sealed record FakeScript(int RunMs = 100, string? Fault = null, string? StartError = null, bool Forever = false);

/// <summary>
/// A script host whose scripts are behaviours, for the queue's logic: records every start with its option profile,
/// ends runs on time or with an error, supports pause, and can play Skua's relogin (stop the run, restart it later).
/// </summary>
internal sealed class QueueScriptHost : IScriptHost
{
    private readonly object _gate = new();
    private ScriptStatus _status = ScriptStatus.Idle;
    private string? _loaded;
    private CancellationTokenSource? _run;

    public Dictionary<string, FakeScript> Scripts { get; } = new(StringComparer.Ordinal);

    public List<(string Name, string? Storage, bool Silent)> Starts { get; } = new();

    /// <summary>Restarts by "Skua's relogin" (not by the queue).</summary>
    public List<string> Restarts { get; } = new();

    public ScriptSupport Support { get; } = new(true, null);

    public ScriptStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public int LoadedAssemblyCount => 0;

    public bool CanPause => true;

    public event EventHandler<ScriptStatus>? StatusChanged;

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public List<string> StartNames
    {
        get
        {
            lock (_gate)
                return Starts.Select(s => s.Name).ToList();
        }
    }

    public void Attach(AqwSession session)
    {
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

    public Task StartAsync(CancellationToken cancellationToken) => StartAsync(ScriptStartOptions.Default, cancellationToken);

    public Task StartAsync(ScriptStartOptions options, CancellationToken cancellationToken)
    {
        string path;
        FakeScript script;
        lock (_gate)
        {
            path = _loaded ?? throw new EngineException("Choose a script first.");
            if (_status.IsBusy)
                throw new EngineException("A script is already running.");
            Starts.Add((Path.GetFileName(path), options.OptionStorage, options.Silent));
            script = Scripts.TryGetValue(Path.GetFileName(path), out FakeScript? s) ? s : new FakeScript();
        }
        if (script.StartError is string error)
        {
            Set(new ScriptStatus(ScriptRunState.Faulted, path, null, error));
            throw new EngineException(error);
        }
        Begin(path, script);
        return Task.CompletedTask;
    }

    private void Begin(string path, FakeScript script)
    {
        CancellationTokenSource run = new();
        lock (_gate)
        {
            _run?.Cancel();
            _run = run;
        }
        Set(new ScriptStatus(ScriptRunState.Running, path, DateTimeOffset.UtcNow, null));
        if (script.Forever)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Paused time does not count (as a real script blocks in its Sleep).
                int left = script.RunMs;
                while (left > 0)
                {
                    await Task.Delay(10, run.Token);
                    if (!Status.Paused)
                        left -= 10;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_gate)
            {
                if (!ReferenceEquals(_run, run))
                    return;
                _run = null;
            }
            Set(script.Fault is string fault
                ? new ScriptStatus(ScriptRunState.Faulted, path, null, fault)
                : new ScriptStatus(ScriptRunState.Idle, path, null, "Stopped."));
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        EndRun("Stopped.");
        return Task.CompletedTask;
    }

    public Task<bool> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_status.State != ScriptRunState.Running)
                throw new EngineException("No script is running.");
            _status = _status with { Paused = paused };
        }
        StatusChanged?.Invoke(this, Status);
        return Task.FromResult(true);
    }

    /// <summary>Skua's relogin: the script is stopped because the connection dropped.</summary>
    public void StopForRelogin() => EndRun("Stopped.");

    /// <summary>Skua's relogin restarts the script it stopped (not a start by the queue).</summary>
    public void RestartBySkua()
    {
        string path;
        lock (_gate)
            path = _loaded!;
        lock (_gate)
            Restarts.Add(Path.GetFileName(path));
        Begin(path, Scripts.TryGetValue(Path.GetFileName(path), out FakeScript? s) ? s : new FakeScript());
    }

    private void EndRun(string message)
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
            if (!_status.IsBusy)
                return;
        }
        run?.Cancel();
        Set(new ScriptStatus(ScriptRunState.Idle, Status.ScriptPath, null, message));
    }

    private void Set(ScriptStatus status)
    {
        lock (_gate)
            _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public ValueTask DisposeAsync()
    {
        EndRun("Stopped.");
        return ValueTask.CompletedTask;
    }
}

internal static class QueueTesting
{
    public static SchedulerOptions Fast { get; } = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(20),
        SettleAfterRun = TimeSpan.FromMilliseconds(150),
        ResumeGrace = TimeSpan.FromMilliseconds(700),
        OnlineGrace = TimeSpan.FromMilliseconds(30),
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>Empty script files in the library (the fake host runs behaviours, not the files).</summary>
    public static string Script(TestApp app, string name)
    {
        string path = Path.Combine(app.Paths.ScriptsDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// fake");
        return path;
    }

    public static QueueStore Store(TestApp app) => new(app.Paths.QueueFile, app.Paths.PlaylistsDir);

    public static ScriptScheduler Scheduler(TestApp app, SchedulerOptions? options = null) =>
        new(app.Engine, Store(app), app.Paths.ScriptsDir, app.Paths.SkuaOptionsDir, options ?? Fast);

    public static async Task UntilIdle(ScriptScheduler q, int timeoutMs = 15000) =>
        await Sandbox.Until(() => !q.IsRunning, timeoutMs);
}
