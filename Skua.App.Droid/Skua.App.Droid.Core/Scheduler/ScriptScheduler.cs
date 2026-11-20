using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scripts;

namespace Skua.App.Droid.Core.Scheduler;

/// <summary>Timings of <see cref="ScriptScheduler"/> (tests shorten them).</summary>
public sealed record SchedulerOptions
{
    /// <summary>How often a running entry is checked.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// After a run ends, wait this long before deciding how it ended (the desktop waits 2 s before the next script).
    /// Skua's relogin stops the script first and closes the connection right after; this lets the drop show.
    /// </summary>
    public TimeSpan SettleAfterRun { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Back online after a drop: how long to wait for Skua's relogin to restart the entry's script by itself before the
    /// queue starts it again (the desktop waits 3 s after the login).
    /// </summary>
    public TimeSpan ResumeGrace { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Before a run while offline: wait this long after the connection is back (the desktop's 3 s).</summary>
    public TimeSpan OnlineGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The queue position is saved at least this often while an entry runs (its stop-after clock).</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The script queue (the desktop's Script Scheduler) on the app's <see cref="IBotEngine"/>: runs entries one after the
/// other, each with its own display name, option profile (Skua option storage <c>Queue_&lt;script&gt;_&lt;id&gt;</c>) and
/// CoreBots profile, unattended. Lives with the engine for the whole process (owned by <see cref="Hosting.EngineHost"/>,
/// which the foreground service keeps alive), not in a ViewModel: it keeps running in the background and survives
/// activity recreation. Its state is saved at every step (<see cref="QueueStore"/>), so a planned engine restart
/// resumes it.
/// </summary>
/// <remarks>
/// <para>Reused from the desktop (Skua.Shared.Avalonia ScriptSchedulerViewModel): the per-entry option storage name
/// and Skua's OverrideStorage/SilentConfig start, the default display names, "wait for the account to reconnect
/// before the next script" (its paused state monitoring), and the relogin rule: a run that ends while the account is
/// disconnected is not counted ("Disconnected"); once back, if Skua's relogin already restarted the same script the
/// queue adopts it ("resumed"), otherwise it starts the same entry again. Nothing is skipped and nothing runs twice.</para>
/// <para>New here: run count / repeat, stop-after time, delay, error rule (skip, stop, retry), plain-English errors,
/// CoreBots profiles, persistence and resume after a memory restart (the desktop has none of these).</para>
/// </remarks>
public sealed class ScriptScheduler : IDisposable
{
    private readonly IBotEngine _engine;
    private readonly QueueStore _store;
    private readonly string _libraryRoot;
    private readonly string _optionsDir;
    private readonly SchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private QueueState _state;
    private QueuePhase _phase = QueuePhase.Idle;
    private string _text;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private QueueStatus _status = QueueStatus.Empty;
    private volatile bool _selfStop;
    private volatile bool _restarting;
    private long _lastOfflineTicks = long.MinValue;
    private int _runsThisStart;
    private DateTimeOffset _lastCheckpoint;

    /// <param name="engine">The engine the queue starts its scripts on.</param>
    /// <param name="store">Queue state and playlists.</param>
    /// <param name="libraryRoot">The script library root (entries are relative to it).</param>
    /// <param name="optionsDir">Skua's options folder (CoreBots profiles are written there).</param>
    /// <param name="options">Timings; null for the defaults.</param>
    /// <param name="time">Clock for delays and elapsed time.</param>
    public ScriptScheduler(IBotEngine engine, QueueStore store, string libraryRoot, string optionsDir, SchedulerOptions? options = null, TimeProvider? time = null)
    {
        _engine = engine;
        _store = store;
        _libraryRoot = Path.GetFullPath(libraryRoot);
        _optionsDir = optionsDir;
        _options = options ?? new SchedulerOptions();
        _time = time ?? TimeProvider.System;
        _state = store.LoadState() ?? new QueueState();
        _text = _state.Entries.Count == 0 ? "The queue is empty." : $"{_state.Entries.Count} {(_state.Entries.Count == 1 ? "entry" : "entries")}.";
        if (_state.Running && !_state.ResumeAfterRestart)
        {
            // The process died while the queue ran (crash, killed): never restart a queue by itself after that.
            _state = _state with { Running = false };
            if (CurrentEntry(_state) is QueueEntry current)
                _state = WithResult(_state, current.Id, ResultOf(_state, current.Id) with { Status = "Interrupted" });
            _text = $"Interrupted (the app was closed) at entry {_state.Index + 1}. Continue resumes there.";
            Persist();
        }
        else if (_state.ResumeAfterRestart)
        {
            _text = $"Stopped by an engine restart at entry {_state.Index + 1}; it resumes after the login. Continue resumes there.";
        }
        _engine.StopRequested += OnStopRequested;
        _engine.SnapshotChanged += OnSnapshot;
        Publish();
    }

    // Every published snapshot that is not online is remembered (when), so a drop and a quick relogin between two
    // polls still counts as a disconnect of the run that was going on.
    private void OnSnapshot(object? sender, EngineSnapshot snapshot)
    {
        if (snapshot.Connection != ConnectionPhase.Online)
            NoteOffline(snapshot);
    }

    private void NoteOffline(EngineSnapshot snapshot)
    {
        long at = snapshot.At.UtcTicks;
        long seen = Interlocked.Read(ref _lastOfflineTicks);
        while (at > seen)
        {
            long previous = Interlocked.CompareExchange(ref _lastOfflineTicks, at, seen);
            if (previous == seen)
                break;
            seen = previous;
        }
    }

    /// <summary>The latest status. Immutable.</summary>
    public QueueStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>Raised when <see cref="Status"/> changed (background thread).</summary>
    public event EventHandler<QueueStatus>? StatusChanged;

    /// <summary>
    /// Asked before every run but the first after a start: returns true when it restarted the engine (memory over the
    /// watchdog threshold and an automatic restart allowed); the queue then resumes at that run after the restart.
    /// Set by <see cref="Hosting.EngineHost"/>.
    /// </summary>
    public Func<string, bool>? RestartBetweenRuns { get; set; }

    /// <summary>The queue runs (as <see cref="Status"/> shows it).</summary>
    public bool IsRunning => Status.IsRunning;

    public IReadOnlyList<QueueEntry> Entries
    {
        get
        {
            lock (_gate)
                return _state.Entries;
        }
    }

    public bool Loop
    {
        get
        {
            lock (_gate)
                return _state.Loop;
        }
        set
        {
            lock (_gate)
                _state = _state with { Loop = value };
            Persist();
            Publish();
        }
    }

    /// <summary>The full path of an entry's script (inside the library).</summary>
    /// <exception cref="EngineException">The path leaves the library.</exception>
    public string FullPath(QueueEntry entry)
    {
        string full = Path.GetFullPath(Path.Combine(_libraryRoot, entry.Script.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_libraryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new EngineException($"{entry.Name}: the script is outside the script folder.");
        return full;
    }

    // ------------------------------------------------------------------ editing (refused while running)

    /// <summary>Adds a script (desktop: a second copy of the same script is named "Name (2).cs").</summary>
    public QueueEntry Add(string scriptPath)
    {
        string full = Path.GetFullPath(scriptPath);
        if (!full.StartsWith(_libraryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new EngineException("Only scripts in the script folder can be queued.");
        string relative = Path.GetRelativePath(_libraryRoot, full).Replace(Path.DirectorySeparatorChar, '/');
        QueueEntry entry;
        lock (_gate)
        {
            ThrowIfRunning("add scripts");
            int count = _state.Entries.Count(e => e.Script == relative);
            string name = count > 0
                ? $"{Path.GetFileNameWithoutExtension(full)} ({count + 1}){Path.GetExtension(full)}"
                : Path.GetFileName(full);
            entry = new QueueEntry { Script = relative, Name = name };
            _state = _state with { Entries = [.. _state.Entries, entry] };
            _text = $"Added {name}.";
        }
        Persist();
        Publish();
        return entry;
    }

    /// <summary>Replaces the entry with the same id (name, runs, limits, error rule, profiles).</summary>
    public void Update(QueueEntry entry)
    {
        lock (_gate)
        {
            ThrowIfRunning("edit entries");
            int i = IndexOf(entry.Id);
            if (i < 0)
                throw new EngineException("That entry is no longer in the queue.");
            QueueEntry fixedEntry = entry.Normalized() with { Script = _state.Entries[i].Script };
            List<QueueEntry> list = _state.Entries.ToList();
            list[i] = fixedEntry;
            _state = _state with { Entries = list };
        }
        Persist();
        Publish();
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            ThrowIfRunning("remove entries");
            int i = IndexOf(id);
            if (i < 0)
                return;
            List<QueueEntry> list = _state.Entries.ToList();
            list.RemoveAt(i);
            Dictionary<Guid, EntryResult> results = _state.Results.Where(r => r.Key != id).ToDictionary();
            _state = _state with { Entries = list, Results = results, Index = Math.Min(_state.Index, list.Count) };
            if (i < _state.Index)
                _state = _state with { Index = _state.Index - 1 };
            _text = list.Count == 0 ? "The queue is empty." : $"{list.Count} {(list.Count == 1 ? "entry" : "entries")}.";
        }
        Persist();
        Publish();
    }

    /// <summary>Moves an entry up (-1) or down (+1).</summary>
    public void Move(Guid id, int delta)
    {
        lock (_gate)
        {
            ThrowIfRunning("reorder entries");
            int i = IndexOf(id);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _state.Entries.Count)
                return;
            List<QueueEntry> list = _state.Entries.ToList();
            (list[i], list[j]) = (list[j], list[i]);
            _state = _state with { Entries = list };
        }
        Persist();
        Publish();
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfRunning("clear the queue");
            _state = new QueueState { Loop = _state.Loop };
            _text = "The queue is empty.";
        }
        Persist();
        Publish();
    }

    /// <summary>
    /// The entry's CoreBots profile for <paramref name="account"/>: CoreBots' settings as currently saved for the
    /// account, with the entry's saved values on top. Edited in the same editor as script options.
    /// </summary>
    public ScriptOptionSet LoadCoreBotsProfile(QueueEntry entry, string account)
    {
        ScriptOptionSet current = CoreBotsOptions.Load(_libraryRoot, _optionsDir, account);
        if (entry.CoreBots is not { } saved)
            return current;
        Dictionary<string, SavedOption> byName = saved.ToDictionary(o => o.Name, StringComparer.Ordinal);
        List<ScriptOptionItem> items = current.Items.Select(i => byName.TryGetValue(i.Name, out SavedOption? o) ? i with { Value = o.Value } : i).ToList();
        foreach (SavedOption extra in saved.Where(o => current.Items.All(i => i.Name != o.Name)))
            items.Add(new ScriptOptionItem("General", extra.Name, extra.Name, string.Empty, extra.Kind, extra.Value, [], false));
        return current with { Items = items };
    }

    /// <summary>Stores <paramref name="set"/> as the entry's CoreBots profile (null: no profile).</summary>
    /// <exception cref="EngineException">A number is not a whole number.</exception>
    public void SetCoreBotsProfile(Guid id, ScriptOptionSet? set)
    {
        List<SavedOption>? saved = null;
        if (set is not null)
        {
            List<string> problems = set.Items
                .Where(i => i.Kind == ScriptOptionKind.Number && i.Value.Trim().Length > 0 && !int.TryParse(i.Value.Trim(), out _))
                .Select(i => $"{i.Title}: \"{i.Value}\" is not a whole number")
                .ToList();
            if (problems.Count > 0)
                throw new EngineException("Not saved. " + string.Join("; ", problems));
            saved = set.Items.Select(i => new SavedOption(i.Name, i.Kind, i.Value.Trim())).ToList();
        }
        QueueEntry entry;
        lock (_gate)
        {
            int i = IndexOf(id);
            if (i < 0)
                throw new EngineException("That entry is no longer in the queue.");
            entry = _state.Entries[i];
        }
        Update(entry with { CoreBots = saved });
    }

    // ------------------------------------------------------------------ playlists

    public IReadOnlyList<string> ListPlaylists() => _store.ListPlaylists();

    /// <exception cref="EngineException">The name is not valid or the queue is empty.</exception>
    public void SavePlaylist(string name)
    {
        Playlist playlist;
        lock (_gate)
        {
            if (_state.Entries.Count == 0)
                throw new EngineException("The queue is empty.");
            playlist = new Playlist(name?.Trim() ?? string.Empty, _state.Entries, _state.Loop);
        }
        try
        {
            _store.SavePlaylist(playlist);
        }
        catch (ArgumentException e)
        {
            throw new EngineException(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EngineException($"Could not save the playlist: {e.Message}");
        }
        lock (_gate)
        {
            _state = _state with { PlaylistName = playlist.Name };
            _text = $"Saved as \"{playlist.Name}\".";
        }
        Persist();
        Publish();
    }

    /// <summary>Replaces the queue with a saved playlist. Entries whose script no longer exists are left out (desktop).</summary>
    public int LoadPlaylist(string name)
    {
        Playlist? playlist;
        try
        {
            playlist = _store.LoadPlaylist(name);
        }
        catch (ArgumentException e)
        {
            throw new EngineException(e.Message);
        }
        if (playlist is null)
            throw new EngineException($"No playlist \"{name}\".");
        List<QueueEntry> entries = new();
        int missing = 0;
        foreach (QueueEntry e in playlist.Entries)
        {
            string? full = TryFullPath(e);
            if (full is not null && File.Exists(full))
                entries.Add(e);
            else
                missing++;
        }
        lock (_gate)
        {
            ThrowIfRunning("load a playlist");
            _state = new QueueState { PlaylistName = playlist.Name, Entries = entries, Loop = playlist.Loop };
            _text = $"Loaded \"{playlist.Name}\": {entries.Count} {(entries.Count == 1 ? "entry" : "entries")}{(missing > 0 ? $" ({missing} missing script{(missing == 1 ? "" : "s")} left out)" : string.Empty)}.";
        }
        Persist();
        Publish();
        return entries.Count;
    }

    public bool DeletePlaylist(string name)
    {
        try
        {
            return _store.DeletePlaylist(name);
        }
        catch (ArgumentException e)
        {
            throw new EngineException(e.Message);
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>Runs the queue from the first entry.</summary>
    /// <exception cref="EngineException">Empty, already running, or a script is running.</exception>
    public void Start() => Begin(fromStart: true, "Starting the queue.");

    /// <summary>Runs the queue from where it stopped (the current entry, its runs so far kept).</summary>
    public void Continue() => Begin(fromStart: false, "Continuing the queue.");

    /// <summary>After a planned engine restart (memory): resumes the queue the restart interrupted, if any.</summary>
    public bool ResumeAfterRestart()
    {
        lock (_gate)
        {
            if (!_state.ResumeAfterRestart || _phase != QueuePhase.Idle)
                return false;
        }
        try
        {
            Begin(fromStart: false, "Resuming the queue after the engine restart.", allowBusy: false);
            return true;
        }
        catch (EngineException e)
        {
            Log(EngineLogLevel.Warning, $"Could not resume the queue after the restart: {e.Message}");
            return false;
        }
    }

    private void Begin(bool fromStart, string text, bool allowBusy = false)
    {
        lock (_gate)
        {
            if (_phase != QueuePhase.Idle)
                throw new EngineException("The queue is already running.");
            if (_state.Entries.Count == 0)
                throw new EngineException("The queue is empty.");
            if (!allowBusy && _engine.Snapshot.Script.IsBusy)
                throw new EngineException("Stop the running script first.");
            if (!_engine.ScriptSupport.Available)
                throw new EngineException(_engine.ScriptSupport.Reason ?? "Scripts are not available.");
            QueueState s = _state;
            if (fromStart || s.Index >= s.Entries.Count)
            {
                s = s with
                {
                    Index = 0,
                    RunsDone = 0,
                    Attempts = 0,
                    EntryElapsed = TimeSpan.Zero,
                    Results = new Dictionary<Guid, EntryResult>(),
                    StartedAt = _time.GetUtcNow(),
                };
            }
            else
            {
                s = s with { StartedAt = s.StartedAt ?? _time.GetUtcNow() };
            }
            _state = s with { Running = true, ResumeAfterRestart = false };
            _phase = QueuePhase.Waiting;
            _text = text;
            _restarting = false;
            _runsThisStart = 0;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(token));
        }
        Log(EngineLogLevel.Info, text);
        Persist();
        Publish();
    }

    /// <summary>Stops the queue and its script. The position is kept (Continue resumes there).</summary>
    public Task StopAsync() => StopCoreAsync("Stopped.", stopScript: true);

    private async Task StopCoreAsync(string text, bool stopScript)
    {
        CancellationTokenSource? cts;
        Task loop;
        lock (_gate)
        {
            if (_phase == QueuePhase.Idle)
                return;
            cts = _cts;
            loop = _loop;
            _phase = QueuePhase.Stopping;
            _text = "Stopping the queue...";
        }
        Publish();
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        QueueEntry? current;
        lock (_gate)
            current = CurrentEntry(_state);
        ScriptStatus script = _engine.Snapshot.Script;
        if (stopScript && current is not null && script.State == ScriptRunState.Running && SamePath(script.ScriptPath, TryFullPath(current)))
            await StopOwnScriptAsync().ConfigureAwait(false);
        if (_restarting)
            return;
        lock (_gate)
        {
            if (current is not null)
                _state = WithResult(_state, current.Id, ResultOf(_state, current.Id) with { Status = "Stopped" });
            _state = _state with { Running = false };
            _phase = QueuePhase.Idle;
            _text = text;
            _cts = null;
        }
        cts?.Dispose();
        Log(EngineLogLevel.Info, "Queue " + text.TrimEnd('.').ToLowerInvariant() + ".");
        Persist();
        Publish();
    }

    /// <summary>
    /// The engine is about to restart (memory watchdog, the restart button, or this queue between runs): save the
    /// position as "resume after the restart" and let the loop go without touching the saved state again.
    /// </summary>
    public void OnRestarting()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_phase == QueuePhase.Idle)
                return;
            _restarting = true;
            _state = _state with { Running = true, ResumeAfterRestart = true };
            _phase = QueuePhase.Restarting;
            _text = $"The engine restarts to free memory; the queue resumes at entry {_state.Index + 1} after the login.";
            cts = _cts;
            _store.SaveState(_state with { SavedAt = _time.GetUtcNow() });
        }
        Log(EngineLogLevel.Info, $"Engine restart: the queue resumes at entry {Status.Index + 1} afterwards.");
        Publish();
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnStopRequested(object? sender, EngineStopRequest request)
    {
        if (_selfStop || _restarting || !IsRunning)
            return;
        // Someone stopped the script (Stop button, notification, another page) or logged out: the queue stops with it.
        _ = StopCoreAsync(request == EngineStopRequest.Logout ? "Stopped: logged out." : "Stopped with the script.", stopScript: false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using IDisposable work = _engine.BeginBackgroundWork("Script queue");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QueueState s;
                lock (_gate)
                    s = _state;

                if (s.Index >= s.Entries.Count)
                {
                    if (s.Loop && s.Entries.Count > 0)
                    {
                        lock (_gate)
                            _state = _state with { Index = 0, RunsDone = 0, Attempts = 0, EntryElapsed = TimeSpan.Zero, Results = new Dictionary<Guid, EntryResult>() };
                        Log(EngineLogLevel.Info, "Queue: starting over (loop).");
                        Persist();
                        continue;
                    }
                    Finish($"Finished: {s.Entries.Count} {(s.Entries.Count == 1 ? "entry" : "entries")} done.");
                    return;
                }

                QueueEntry entry = s.Entries[s.Index];
                if ((entry.Runs > 0 && s.RunsDone >= entry.Runs) || (entry.StopAfter is TimeSpan limit && s.EntryElapsed >= limit))
                {
                    Advance(entry, "Completed", null);
                    continue;
                }

                await WaitOnlineAsync(entry, ct).ConfigureAwait(false);

                if (_runsThisStart > 0 && RestartBetweenRuns is Func<string, bool> restart
                    && restart($"memory over the threshold before queue entry {s.Index + 1} ({entry.Name})"))
                {
                    // EngineHost raised RestartStarting: OnRestarting saved the position. Nothing else to do here.
                    return;
                }

                RunResult result = await RunOnceAsync(entry, ct).ConfigureAwait(false);
                _runsThisStart++;
                if (result.Kind == RunKind.Cancelled)
                    return;

                QueueState after;
                lock (_gate)
                    after = _state;
                if (result.Kind == RunKind.Completed)
                {
                    int runs = after.RunsDone + 1;
                    bool done = result.TimeUp || (entry.Runs > 0 && runs >= entry.Runs) || (entry.StopAfter is TimeSpan l && after.EntryElapsed >= l);
                    if (done)
                    {
                        Log(EngineLogLevel.Info, $"Queue: {entry.Name} completed{(result.TimeUp ? " (stop-after time reached)" : string.Empty)}.");
                        lock (_gate)
                            _state = _state with { RunsDone = runs };
                        Advance(entry, "Completed", null);
                    }
                    else
                    {
                        lock (_gate)
                        {
                            _state = _state with { RunsDone = runs, Attempts = 0 };
                            _state = WithResult(_state, entry.Id, ResultOf(_state, entry.Id) with { Status = "Queued", RunsDone = runs, Error = null });
                        }
                        Log(EngineLogLevel.Info, $"Queue: {entry.Name} run {runs}{(entry.Runs > 0 ? $"/{entry.Runs}" : string.Empty)} done.");
                        Persist();
                    }
                }
                else
                {
                    string error = result.Error ?? "The script stopped with an error.";
                    Log(EngineLogLevel.Warning, $"Queue: {entry.Name}: {error}");
                    if (entry.OnError == QueueErrorAction.Retry && after.Attempts < entry.MaxRetries)
                    {
                        lock (_gate)
                        {
                            _state = _state with { Attempts = after.Attempts + 1 };
                            _state = WithResult(_state, entry.Id, ResultOf(_state, entry.Id) with { Status = "Retrying", Error = $"{error} (retry {after.Attempts + 1} of {entry.MaxRetries})" });
                        }
                        Persist();
                    }
                    else if (entry.OnError == QueueErrorAction.Stop)
                    {
                        lock (_gate)
                        {
                            _state = WithResult(_state, entry.Id, ResultOf(_state, entry.Id) with { Status = result.Status, Error = error });
                            _state = _state with { Running = false };
                            _phase = QueuePhase.Idle;
                            _text = $"Stopped: {entry.Name} failed. {error}";
                        }
                        Log(EngineLogLevel.Warning, $"Queue stopped: {entry.Name} failed.");
                        Persist();
                        Publish();
                        return;
                    }
                    else
                    {
                        Advance(entry, result.Status, error);
                    }
                }

                // Wait before the next run (also after the last entry of a looping queue).
                QueueState next;
                lock (_gate)
                    next = _state;
                if (next.Index < next.Entries.Count || next.Loop)
                {
                    SetPhase(QueuePhase.Waiting, $"Waiting {QueueText.Duration(entry.DelayAfter)} before {NameAt(next, next.Index) ?? NameAt(next, 0)}.");
                    await Task.Delay(entry.DelayAfter, _time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // Never take the process down for the queue: stop it and say why.
            Log(EngineLogLevel.Error, $"Queue failed: {e.GetType().Name}: {e.Message}");
            lock (_gate)
            {
                _state = _state with { Running = false };
                _phase = QueuePhase.Idle;
                _text = $"Stopped: {e.Message}";
            }
            Persist();
            Publish();
        }
    }

    private enum RunKind
    {
        Completed,
        Failed,
        Cancelled,
    }

    private sealed record RunResult(RunKind Kind, string Status = "Completed", string? Error = null, bool TimeUp = false)
    {
        public static RunResult Done { get; } = new(RunKind.Completed);

        public static RunResult Cancelled { get; } = new(RunKind.Cancelled);
    }

    /// <summary>One run of <paramref name="entry"/>, including its resumption after a relogin.</summary>
    private async Task<RunResult> RunOnceAsync(QueueEntry entry, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? path = TryFullPath(entry);
            if (path is null || !File.Exists(path))
                return new RunResult(RunKind.Failed, "File Not Found", ErrorText.Summarize("No script at " + entry.Script));

            ApplyCoreBotsProfile(entry);
            SetPhase(QueuePhase.Running, $"Starting {entry.Name}...");

            bool adopted = false;
            // Engine snapshots are stamped with the system clock: a disconnect seen after this belongs to this run.
            DateTimeOffset runStarted = DateTimeOffset.UtcNow;
            ScriptStatus busy = _engine.Snapshot.Script;
            if (busy.IsBusy)
            {
                if (SamePath(busy.ScriptPath, path))
                {
                    adopted = true;
                }
                else
                {
                    // The desktop's "stopping lingering script before launching the scheduled one".
                    Log(EngineLogLevel.Info, $"Queue: stopping {busy.ScriptName} before {entry.Name}.");
                    await StopOwnScriptAsync().ConfigureAwait(false);
                }
            }
            if (!adopted)
            {
                try
                {
                    QueueStatus at = Status;
                    Log(EngineLogLevel.Info, $"Queue: starting {entry.Name} ({at.Index + 1}/{at.Count}).");
                    Task start = _engine.StartScriptAsync(path, new ScriptStartOptions(entry.OptionStorage, Silent: true), ct);
                    // "Running" only once the engine has the start: a pause by whoever sees it then waits for the
                    // start (or finds the script running) instead of finding no script.
                    SetResult(entry, "Running", null);
                    await start.ConfigureAwait(false);
                }
                catch (EngineException e)
                {
                    ScriptStatus now = _engine.Snapshot.Script;
                    if (!(now.IsBusy && SamePath(now.ScriptPath, path)))
                    {
                        return new RunResult(RunKind.Failed, "Failed", ErrorText.Summarize(e.Message));
                    }
                    // Skua's relogin restarted the same script in the meantime: that is this run.
                }
            }
            else
            {
                SetResult(entry, "Running", null);
            }

            if (adopted && busy.StartedAt is DateTimeOffset adoptedStart)
                runStarted = adoptedStart;
            RunResult? outcome = await MonitorAsync(entry, path, runStarted, ct).ConfigureAwait(false);
            if (outcome is not null)
                return outcome;
            // null: the account was disconnected and Skua did not bring the script back: start the same run again.
            Log(EngineLogLevel.Info, $"Queue: back online; starting {entry.Name} again (the run did not finish).");
        }
    }

    /// <summary>Follows a run until it ends. Null when it must be started again (disconnected, not restarted by Skua).</summary>
    private async Task<RunResult?> MonitorAsync(QueueEntry entry, string path, DateTimeOffset runStarted, CancellationToken ct)
    {
        DateTimeOffset last = _time.GetUtcNow();
        // When the script's current run started (a relogin restart by Skua starts a new one): a disconnect seen after it
        // means the run ended because of the drop, even if the account is already back when the end is noticed.
        long runStartedTicks = runStarted.UtcTicks;
        while (true)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            EngineSnapshot snap = _engine.Snapshot;
            ScriptStatus s = snap.Script;
            DateTimeOffset now = _time.GetUtcNow();
            if (s.IsBusy && SamePath(s.ScriptPath, path))
            {
                // Skua's relogin restart is a new run of the same entry: only drops after it count from then on.
                if (s.StartedAt is DateTimeOffset started && started.UtcTicks > runStartedTicks)
                    runStartedTicks = started.UtcTicks;
                if (snap.Connection != ConnectionPhase.Online)
                    NoteOffline(snap);
                TimeSpan elapsed = AddElapsed(s.State == ScriptRunState.Running && !s.Paused ? now - last : TimeSpan.Zero);
                last = now;
                if (s.IsPaused)
                {
                    SetResult(entry, "Paused", null);
                    SetPhase(QueuePhase.Running, $"{entry.Name} is paused; the queue waits.");
                }
                else
                {
                    SetResult(entry, "Running", null);
                    SetPhase(QueuePhase.Running, $"Running {entry.Name}.");
                }
                if (entry.StopAfter is TimeSpan limit && elapsed >= limit && s.State == ScriptRunState.Running)
                {
                    Log(EngineLogLevel.Info, $"Queue: {entry.Name} reached its stop-after time ({QueueText.Duration(limit)}).");
                    await StopOwnScriptAsync().ConfigureAwait(false);
                    return new RunResult(RunKind.Completed, TimeUp: true);
                }
                if (now - _lastCheckpoint >= _options.CheckpointInterval)
                {
                    _lastCheckpoint = now;
                    Persist();
                }
                continue;
            }
            last = now;

            // The run ended (or another script replaced it). Let a disconnect show before deciding how.
            ScriptStatus ended = s;
            await Task.Delay(_options.SettleAfterRun, _time, ct).ConfigureAwait(false);
            snap = _engine.Snapshot;
            if (snap.Script.IsBusy && SamePath(snap.Script.ScriptPath, path))
                continue; // Skua restarted it already: the same run goes on
            bool dropped = Interlocked.Read(ref _lastOfflineTicks) >= runStartedTicks;
            if (snap.Connection != ConnectionPhase.Online || dropped)
            {
                bool resumed = await WaitForResumptionAsync(entry, path, ct).ConfigureAwait(false);
                if (resumed)
                {
                    last = _time.GetUtcNow();
                    runStartedTicks = Math.Max(runStartedTicks, (_engine.Snapshot.Script.StartedAt ?? DateTimeOffset.UtcNow).UtcTicks);
                    continue;
                }
                return null;
            }
            if (ended.State == ScriptRunState.Faulted && SamePath(ended.ScriptPath, path))
                return new RunResult(RunKind.Failed, ended.Message?.StartsWith("Compile error", StringComparison.Ordinal) == true ? "Failed" : "Crashed", ErrorText.Summarize(ended.Message));
            return RunResult.Done;
        }
    }

    /// <summary>
    /// The account is disconnected and the entry's script is not running: wait for the connection. True when Skua's
    /// relogin restarted the same script (the run goes on, nothing to do); false when the account is back and the
    /// script was not restarted within <see cref="SchedulerOptions.ResumeGrace"/> (the queue starts it again).
    /// </summary>
    private async Task<bool> WaitForResumptionAsync(QueueEntry entry, string path, CancellationToken ct)
    {
        SetResult(entry, "Disconnected", null);
        SetPhase(QueuePhase.WaitingForConnection, $"Disconnected during {entry.Name}: waiting for the relogin.");
        Log(EngineLogLevel.Warning, $"Queue: disconnected during {entry.Name}; waiting for the relogin (the entry is not skipped).");
        Persist();
        DateTimeOffset? onlineSince = null;
        while (true)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            EngineSnapshot snap = _engine.Snapshot;
            if (snap.Script.IsBusy && SamePath(snap.Script.ScriptPath, path))
            {
                Log(EngineLogLevel.Info, $"Queue: {entry.Name} resumed after the reconnect (restarted by Skua's relogin).");
                SetResult(entry, "Running", null);
                return true;
            }
            DateTimeOffset now = _time.GetUtcNow();
            if (snap.Connection == ConnectionPhase.Online && !snap.Script.IsBusy)
            {
                onlineSince ??= now;
                if (now - onlineSince.Value >= _options.ResumeGrace)
                    return false;
            }
            else
            {
                onlineSince = null;
            }
        }
    }

    /// <summary>Before a run: the account must be online (the desktop's "waiting for account to reconnect").</summary>
    private async Task WaitOnlineAsync(QueueEntry entry, CancellationToken ct)
    {
        if (_engine.Snapshot.Connection == ConnectionPhase.Online)
            return;
        SetPhase(QueuePhase.WaitingForConnection, $"Waiting for the connection before {entry.Name}.");
        Log(EngineLogLevel.Info, $"Queue: waiting for the connection before {entry.Name}.");
        while (_engine.Snapshot.Connection != ConnectionPhase.Online)
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        await Task.Delay(_options.OnlineGrace, _time, ct).ConfigureAwait(false);
    }

    private async Task StopOwnScriptAsync()
    {
        _selfStop = true;
        try
        {
            await _engine.StopScriptAsync().ConfigureAwait(false);
        }
        catch (EngineException e)
        {
            Log(EngineLogLevel.Warning, $"Queue: stopping the script: {e.Message}");
        }
        finally
        {
            _selfStop = false;
        }
    }

    private void ApplyCoreBotsProfile(QueueEntry entry)
    {
        if (entry.CoreBots is not { Count: > 0 } saved)
            return;
        string? account = _engine.Snapshot.Character?.Name ?? _engine.Snapshot.AccountName;
        if (!CoreBotsOptions.IsSafeAccountName(account))
        {
            Log(EngineLogLevel.Warning, $"Queue: {entry.Name}: no account to apply the CoreBots profile to.");
            return;
        }
        try
        {
            string file = CoreBotsOptions.FileFor(_optionsDir, account!);
            ScriptOptionSet set = new(Path.Combine(_libraryRoot, CoreBotsOptions.CoreBotsFile), Path.GetFileNameWithoutExtension(file),
                saved.Select(o => new ScriptOptionItem("General", o.Name, o.Name, string.Empty, o.Kind, o.Value, [], false)).ToList());
            CoreBotsOptions.Save(set, _optionsDir);
        }
        catch (Exception e) when (e is EngineException or IOException or UnauthorizedAccessException)
        {
            Log(EngineLogLevel.Warning, $"Queue: {entry.Name}: the CoreBots profile was not applied: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ state helpers

    private void Advance(QueueEntry entry, string status, string? error)
    {
        lock (_gate)
        {
            EntryResult r = ResultOf(_state, entry.Id);
            _state = WithResult(_state, entry.Id, r with { Status = status, Error = error, RunsDone = _state.RunsDone, Elapsed = _state.EntryElapsed });
            _state = _state with { Index = _state.Index + 1, RunsDone = 0, Attempts = 0, EntryElapsed = TimeSpan.Zero };
        }
        Persist();
        Publish();
    }

    private void Finish(string text)
    {
        lock (_gate)
        {
            _state = _state with { Running = false };
            _phase = QueuePhase.Idle;
            _text = text;
        }
        Log(EngineLogLevel.Info, "Queue " + text[0].ToString().ToLowerInvariant() + text[1..]);
        Persist();
        Publish();
    }

    private TimeSpan AddElapsed(TimeSpan delta)
    {
        lock (_gate)
        {
            _state = _state with { EntryElapsed = _state.EntryElapsed + delta };
            if (CurrentEntry(_state) is QueueEntry e)
                _state = WithResult(_state, e.Id, ResultOf(_state, e.Id) with { Elapsed = _state.EntryElapsed });
            return _state.EntryElapsed;
        }
    }

    private void SetResult(QueueEntry entry, string status, string? error)
    {
        lock (_gate)
        {
            EntryResult r = ResultOf(_state, entry.Id);
            if (r.Status == status && r.Error == error)
                return;
            _state = WithResult(_state, entry.Id, r with { Status = status, Error = error ?? (status == "Running" ? null : r.Error) });
        }
        Publish();
    }

    private void SetPhase(QueuePhase phase, string text)
    {
        lock (_gate)
        {
            if (_phase is QueuePhase.Idle or QueuePhase.Stopping or QueuePhase.Restarting)
                return;
            _phase = phase;
            _text = text;
        }
        Publish();
    }

    private void Persist()
    {
        if (_restarting)
            return;
        QueueState s;
        lock (_gate)
            s = _state with { SavedAt = _time.GetUtcNow() };
        try
        {
            _store.SaveState(s);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(EngineLogLevel.Warning, $"Queue: could not save its state: {e.Message}");
        }
    }

    private void Publish()
    {
        QueueStatus status;
        lock (_gate)
        {
            QueueState s = _state;
            QueueEntry? current = CurrentEntry(s);
            bool running = _phase != QueuePhase.Idle;
            List<QueueEntryView> views = s.Entries.Select((e, i) => new QueueEntryView(i, e, ResultOf(s, e.Id), running && i == s.Index)).ToList();
            string? next = s.Index + 1 < s.Entries.Count ? s.Entries[s.Index + 1].Name : s.Loop && s.Entries.Count > 0 ? s.Entries[0].Name : null;
            status = new QueueStatus(
                _phase,
                _text,
                s.Index,
                s.Entries.Count,
                current?.Name,
                next,
                current is null ? 0 : s.RunsDone + 1,
                current?.Runs ?? 0,
                s.StartedAt,
                s.EntryElapsed,
                s.Loop,
                s.PlaylistName,
                views);
            _status = status;
        }
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // A broken subscriber must not stop the queue.
        }
    }

    private void Log(EngineLogLevel level, string message) => _engine.WriteLog(level, "queue", message);

    private void ThrowIfRunning(string what)
    {
        if (_phase != QueuePhase.Idle)
            throw new EngineException($"Stop the queue to {what}.");
    }

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < _state.Entries.Count; i++)
        {
            if (_state.Entries[i].Id == id)
                return i;
        }
        return -1;
    }

    private string? TryFullPath(QueueEntry entry)
    {
        try
        {
            return FullPath(entry);
        }
        catch (Exception e) when (e is EngineException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static QueueEntry? CurrentEntry(QueueState s) => s.Index >= 0 && s.Index < s.Entries.Count ? s.Entries[s.Index] : null;

    private static string? NameAt(QueueState s, int index) => index >= 0 && index < s.Entries.Count ? s.Entries[index].Name : null;

    private static EntryResult ResultOf(QueueState s, Guid id) => s.Results.TryGetValue(id, out EntryResult? r) ? r : EntryResult.Queued;

    private static QueueState WithResult(QueueState s, Guid id, EntryResult result)
    {
        Dictionary<Guid, EntryResult> results = new(s.Results) { [id] = result };
        return s with { Results = results };
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        _engine.StopRequested -= OnStopRequested;
        _engine.SnapshotChanged -= OnSnapshot;
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _cts;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
