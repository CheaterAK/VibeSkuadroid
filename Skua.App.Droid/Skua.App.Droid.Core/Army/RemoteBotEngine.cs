using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Army;

/// <summary>
/// The coordinator's <see cref="IBotEngine"/> for a full slot running in its own process: requests go over an
/// <see cref="ISlotChannel"/>, snapshots and log lines come back as events. What cannot cross the process boundary
/// (Skua's services, the plugins' backends, packet capture, raw packets, the bank) is absent here: the slot's
/// detail page offers what this proxy supports.
/// </summary>
/// <remarks>
/// Logging in never sends a password: <see cref="LoginAccountAsync"/> names the account and the slot reads its
/// password from the Keystore vault itself. <see cref="IBotEngine.LoginAsync"/> with a password is refused. Script
/// paths are sent relative to the script folder (each slot resolves them in its own copy of the shared scripts).
/// </remarks>
public sealed class RemoteBotEngine : IBotEngine
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(5);

    private readonly ISlotChannel _channel;
    private readonly string? _scriptsRoot;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<SlotMessage>> _pending = new();
    private readonly RingBuffer<LogEntry> _log = new(600);
    private int _nextId;
    private volatile EngineSnapshot _snapshot;
    private volatile WorldLists _lists = WorldLists.Empty;
    private volatile string? _accountId;
    private volatile string? _queueText;
    private bool _autoReconnect = true;

    /// <param name="slot">The slot number (for messages).</param>
    /// <param name="channel">The link to the slot process.</param>
    /// <param name="scriptsRoot">The coordinator's script folder: absolute script paths under it are sent relative.</param>
    public RemoteBotEngine(int slot, ISlotChannel channel, string? scriptsRoot = null)
    {
        Slot = slot;
        _channel = channel;
        _scriptsRoot = scriptsRoot;
        _snapshot = Offline("Slot not started");
        _channel.Received += OnReceived;
        _channel.ConnectionChanged += OnConnectionChanged;
    }

    public int Slot { get; }

    public bool IsConnected => _channel.IsConnected;

    /// <summary>The account the slot reported in its last status.</summary>
    public string? AccountId => _accountId;

    /// <summary>The slot's script queue line, if a queue runs there.</summary>
    public string? QueueText => _queueText;

    public EngineSnapshot Snapshot => _snapshot;

    public event EventHandler<EngineSnapshot>? SnapshotChanged;

    public event EventHandler<LogEntry>? LogAdded;

    public event EventHandler<PacketEntry>? PacketLogged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<LogEntry> GetRecentLog() => _log.Snapshot();

    public void WriteLog(EngineLogLevel level, string source, string message) => AddLog(new LogEntry(DateTimeOffset.Now, level, source, message));

    public void ClearLog() => _log.Clear();

    public IReadOnlyList<PacketEntry> GetRecentPackets() => [];

    public void ClearPackets()
    {
    }

    public bool PacketCaptureEnabled
    {
        get => false;
        set { }
    }

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            _autoReconnect = value;
            _ = RequestQuietlyAsync(SlotOps.AutoReconnect, new JsonObject { ["on"] = value });
        }
    }

    public ScriptSupport ScriptSupport { get; } = new(true, null);

    public bool CanPauseScript => true;

    /// <summary>Asks the slot who it is and for its state (after the link came up).</summary>
    public async Task<SlotHello> HelloAsync(CancellationToken cancellationToken = default)
    {
        SlotHello hello = SlotProtocol.FromNode<SlotHello>(await RequestAsync(SlotOps.Hello, null, DefaultTimeout, cancellationToken).ConfigureAwait(false))
            ?? throw new EngineException($"Slot {Slot} did not say who it is.");
        if (hello.Slot != Slot)
            throw new EngineException($"Slot {Slot} answered as slot {hello.Slot}.");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return hello;
    }

    /// <summary>Reads the slot's status and its recent log now.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ApplyStatus(SlotProtocol.FromNode<SlotStatus>(await RequestAsync(SlotOps.Snapshot, null, DefaultTimeout, cancellationToken).ConfigureAwait(false)));
        List<LogEntry>? lines = SlotProtocol.FromNode<List<LogEntry>>(await RequestAsync(SlotOps.Log, new JsonObject { ["max"] = SlotProtocol.MaxLogLines }, DefaultTimeout, cancellationToken).ConfigureAwait(false));
        if (lines is not null)
        {
            _log.Clear();
            foreach (LogEntry line in lines)
                _log.Add(line);
        }
    }

    public async Task<IReadOnlyList<ServerChoice>> GetServersAsync(CancellationToken cancellationToken = default) =>
        SlotProtocol.FromNode<List<ServerChoice>>(await RequestAsync(SlotOps.Servers, null, DefaultTimeout, cancellationToken).ConfigureAwait(false)) ?? [];

    public Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        throw new EngineException("A slot process logs in with the account's remembered password; choose the account instead.");

    /// <summary>Logs the slot in as the account <paramref name="accountId"/> (its password stays in the vault).</summary>
    public Task LoginAccountAsync(string accountId, string? server, CancellationToken cancellationToken = default) =>
        RequestAsync(SlotOps.Login, new JsonObject { ["accountId"] = accountId, ["server"] = server }, LoginTimeout, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) => RequestAsync(SlotOps.Logout, null, DefaultTimeout, cancellationToken);

    public Task JoinMapAsync(string map, string cell = "Enter", string pad = "Spawn", CancellationToken cancellationToken = default) =>
        RequestAsync(SlotOps.JoinMap, new JsonObject { ["map"] = map, ["cell"] = cell, ["pad"] = pad }, DefaultTimeout, cancellationToken);

    /// <summary>/goto a player.</summary>
    public Task GotoPlayerAsync(string name, CancellationToken cancellationToken = default) =>
        RequestAsync(SlotOps.GotoPlayer, new JsonObject { ["name"] = name }, DefaultTimeout, cancellationToken);

    /// <summary>Accepts quests (Butler quest sync); answers how many accept packets were sent.</summary>
    public async Task<int> AcceptQuestsAsync(IEnumerable<int> questIds, CancellationToken cancellationToken = default) =>
        (await RequestAsync(SlotOps.AcceptQuests, new JsonObject { ["ids"] = SlotProtocol.ToNode(questIds.ToArray()) }, DefaultTimeout, cancellationToken).ConfigureAwait(false))?.GetValue<int>() ?? 0;

    /// <summary>Restarts the slot's process (memory); the slot logs back in by itself.</summary>
    public Task RestartAsync(string reason, CancellationToken cancellationToken = default) =>
        RequestAsync(SlotOps.Restart, new JsonObject { ["reason"] = reason }, DefaultTimeout, cancellationToken);

    /// <summary>Raw packets do not cross the process boundary.</summary>
    public Task<bool> SendPacketAsync(string packet, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task LoadBankAsync(CancellationToken cancellationToken = default) =>
        throw new EngineException("The bank of a slot process is not shown here.");

    /// <summary>The last lists fetched with <see cref="FetchWorldListsAsync"/> (quests, drops, monsters; no inventory).</summary>
    public WorldLists GetWorldLists() => _lists;

    public async Task<WorldLists> FetchWorldListsAsync(CancellationToken cancellationToken = default)
    {
        WorldLists lists = SlotProtocol.FromNode<WorldLists>(await RequestAsync(SlotOps.WorldLists, null, DefaultTimeout, cancellationToken).ConfigureAwait(false)) ?? WorldLists.Empty;
        _lists = lists;
        return lists;
    }

    public void LoadScript(string scriptPath) =>
        _ = RequestQuietlyAsync(SlotOps.LoadScript, new JsonObject { ["path"] = Relative(scriptPath) });

    public Task StartScriptAsync(string? scriptPath = null, CancellationToken cancellationToken = default) =>
        scriptPath is null
            ? throw new EngineException("Choose the script to start on the slot.")
            : StartScriptAsync(scriptPath, ScriptStartOptions.Default, cancellationToken);

    public Task StartScriptAsync(string scriptPath, ScriptStartOptions options, CancellationToken cancellationToken = default) =>
        RequestAsync(SlotOps.StartScript, new JsonObject
        {
            ["path"] = Relative(scriptPath),
            ["optionStorage"] = options.OptionStorage,
            ["silent"] = options.Silent,
        }, ScriptTimeout, cancellationToken);

    public Task StopScriptAsync(CancellationToken cancellationToken = default) => RequestAsync(SlotOps.StopScript, null, DefaultTimeout, cancellationToken);

    public Task PauseScriptAsync(CancellationToken cancellationToken = default) => RequestAsync(SlotOps.Pause, null, ScriptTimeout, cancellationToken);

    public Task ResumeScriptAsync(CancellationToken cancellationToken = default) => RequestAsync(SlotOps.Resume, null, DefaultTimeout, cancellationToken);

    /// <summary>The path the slot resolves: relative to the script folder, '/'-separated.</summary>
    public string Relative(string scriptPath)
    {
        if (_scriptsRoot is not null && Path.IsPathRooted(scriptPath))
        {
            string root = Path.GetFullPath(_scriptsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(scriptPath);
            if (!full.StartsWith(root, StringComparison.Ordinal))
                throw new EngineException("The script is not in the script folder.");
            scriptPath = full[root.Length..];
        }
        return scriptPath.Replace('\\', '/');
    }

    private async Task<JsonNode?> RequestAsync(string op, JsonObject? args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<SlotMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            byte[] bytes = SlotProtocol.Encode(new SlotMessage { Kind = SlotMessageKind.Request, Id = id, Op = op, Args = args });
            if (!_channel.Send(bytes))
                throw new EngineException($"Slot {Slot} is not running.");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            SlotMessage response;
            try
            {
                response = await tcs.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EngineException($"Slot {Slot} did not answer ({op}) within {timeout.TotalSeconds:0} s.");
            }
            if (!response.Ok)
                throw new EngineException(response.Error ?? $"Slot {Slot} refused {op}.");
            return response.Result;
        }
        catch (SlotProtocolException e)
        {
            throw new EngineException($"Slot {Slot}: {e.Message}", e);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task RequestQuietlyAsync(string op, JsonObject? args)
    {
        try
        {
            await RequestAsync(op, args, DefaultTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (EngineException e)
        {
            WriteLog(EngineLogLevel.Warning, "army", e.Message);
        }
    }

    private void OnReceived(byte[] bytes)
    {
        SlotMessage message;
        try
        {
            message = SlotProtocol.Decode(bytes);
        }
        catch (SlotProtocolException e)
        {
            WriteLog(EngineLogLevel.Warning, "army", $"Slot {Slot} sent an unreadable message: {e.Message}");
            return;
        }
        switch (message.Kind)
        {
            case SlotMessageKind.Response:
                if (_pending.TryRemove(message.Id, out TaskCompletionSource<SlotMessage>? tcs))
                    tcs.TrySetResult(message);
                break;
            case SlotMessageKind.Event when message.Op == SlotEvents.Snapshot:
                try
                {
                    ApplyStatus(SlotProtocol.FromNode<SlotStatus>(message.Result));
                }
                catch (SlotProtocolException)
                {
                }
                break;
            case SlotMessageKind.Event when message.Op == SlotEvents.Log:
                try
                {
                    foreach (LogEntry line in SlotProtocol.FromNode<List<LogEntry>>(message.Result) ?? [])
                        AddLog(line);
                }
                catch (SlotProtocolException)
                {
                }
                break;
        }
    }

    private void ApplyStatus(SlotStatus? status)
    {
        if (status is null || status.Slot != Slot)
            return;
        _accountId = status.AccountId;
        _queueText = status.QueueText;
        _snapshot = status.Snapshot;
        SnapshotChanged?.Invoke(this, status.Snapshot);
    }

    private void AddLog(LogEntry line)
    {
        _log.Add(line);
        LogAdded?.Invoke(this, line);
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await HelloAsync().ConfigureAwait(false);
                }
                catch (EngineException e)
                {
                    WriteLog(EngineLogLevel.Warning, "army", e.Message);
                }
            });
            return;
        }
        foreach (int id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<SlotMessage>? tcs))
                tcs.TrySetResult(new SlotMessage { Kind = SlotMessageKind.Response, Id = id, Ok = false, Op = "?", Error = $"Slot {Slot} stopped." });
        }
        _snapshot = Offline("Slot process stopped");
        SnapshotChanged?.Invoke(this, _snapshot);
    }

    private static EngineSnapshot Offline(string text) => EngineSnapshot.Initial with { StatusText = text, At = DateTimeOffset.UtcNow };

    public async ValueTask DisposeAsync()
    {
        _channel.Received -= OnReceived;
        _channel.ConnectionChanged -= OnConnectionChanged;
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
