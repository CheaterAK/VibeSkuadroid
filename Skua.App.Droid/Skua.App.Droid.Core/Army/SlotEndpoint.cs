using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Army;

/// <summary>What the slot process gives its <see cref="SlotEndpoint"/>: the engine and the slot-local actions.</summary>
public interface ISlotHost
{
    int Slot { get; }

    IBotEngine Engine { get; }

    /// <summary>The account this slot logged in last (null when none or after a logout).</summary>
    string? AccountId { get; }

    /// <summary>
    /// Logs the account in, reading its password from the Keystore vault inside this process (the coordinator never
    /// sends one). Throws <see cref="EngineException"/> with a user-presentable message.
    /// </summary>
    Task LoginAsync(string accountId, string? server, CancellationToken cancellationToken);

    /// <summary>The script's full path in this slot's script folder; throws <see cref="EngineException"/> for a path outside it.</summary>
    string ResolveScript(string relativePath);

    /// <summary>Copies the shared option profile <paramref name="storage"/> into this slot's options folder (if it exists).</summary>
    void PrepareOptionProfile(string storage);

    /// <summary>Restarts this slot's process (memory); false when refused now (a script runs).</summary>
    bool RequestRestart(string reason);

    /// <summary>The slot's script queue line, if a queue runs.</summary>
    string? QueueText => null;
}

/// <summary>
/// The slot side of the coordinator protocol: decodes a request, runs it on the slot's engine, encodes the answer; and
/// pushes coalesced status snapshots and batched log lines as events. Platform neutral: the Android slot service feeds
/// it Binder messages, the tests an in-memory channel.
/// </summary>
public sealed class SlotEndpoint : IDisposable
{
    private readonly ISlotHost _host;
    private readonly TimeProvider _time;
    private readonly TimeSpan _eventInterval;
    private readonly object _gate = new();
    private readonly List<LogEntry> _pendingLog = new();
    private readonly CancellationTokenSource _lifetime = new();
    private EngineSnapshot? _pendingSnapshot;
    private bool _flushScheduled;

    public SlotEndpoint(ISlotHost host, TimeProvider? time = null, TimeSpan? eventInterval = null)
    {
        _host = host;
        _time = time ?? TimeProvider.System;
        _eventInterval = eventInterval ?? TimeSpan.FromMilliseconds(500);
        _host.Engine.SnapshotChanged += OnSnapshot;
        _host.Engine.LogAdded += OnLog;
    }

    /// <summary>An encoded event for every connected coordinator (raised on a background thread).</summary>
    public event Action<byte[]>? EventReady;

    /// <summary>Answers one encoded request. Never throws: a bad request gets an error response (or none if it cannot be read).</summary>
    public async Task<byte[]?> HandleAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        SlotMessage message;
        try
        {
            message = SlotProtocol.Decode(request);
        }
        catch (SlotProtocolException)
        {
            return null; // not answerable: no id to correlate
        }
        if (message.Kind != SlotMessageKind.Request)
            return null;
        SlotMessage response;
        try
        {
            JsonNode? result = await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            response = new SlotMessage { Kind = SlotMessageKind.Response, Id = message.Id, Op = message.Op, Ok = true, Result = result };
        }
        catch (Exception e) when (e is EngineException or SlotProtocolException or OperationCanceledException)
        {
            string text = e is OperationCanceledException ? "Cancelled." : e.Message;
            response = new SlotMessage { Kind = SlotMessageKind.Response, Id = message.Id, Op = message.Op, Ok = false, Error = text };
        }
        catch (Exception e)
        {
            // Unexpected: the type only (messages of unknown exceptions are not checked for secrets).
            response = new SlotMessage { Kind = SlotMessageKind.Response, Id = message.Id, Op = message.Op, Ok = false, Error = $"The slot failed ({e.GetType().Name})." };
        }
        try
        {
            return SlotProtocol.Encode(response);
        }
        catch (SlotProtocolException e)
        {
            return SlotProtocol.Encode(response with { Ok = false, Result = null, Error = e.Message });
        }
    }

    private async Task<JsonNode?> DispatchAsync(SlotMessage m, CancellationToken ct)
    {
        IBotEngine engine = _host.Engine;
        JsonObject? a = m.Args;
        switch (m.Op)
        {
            case SlotOps.Hello:
                return SlotProtocol.ToNode(new SlotHello(_host.Slot, Environment.ProcessId, SlotProtocol.Version, _host.AccountId));
            case SlotOps.Snapshot:
                return SlotProtocol.ToNode(Status(engine.Snapshot));
            case SlotOps.Log:
                {
                    int max = Math.Clamp(SlotProtocol.GetInt(a, "max", SlotProtocol.MaxLogLines), 1, SlotProtocol.MaxLogLines);
                    IReadOnlyList<LogEntry> all = engine.GetRecentLog();
                    return SlotProtocol.ToNode(Trim(all.Skip(Math.Max(0, all.Count - max)).ToList()));
                }
            case SlotOps.Servers:
                return SlotProtocol.ToNode(await engine.GetServersAsync(ct).ConfigureAwait(false));
            case SlotOps.Login:
                await _host.LoginAsync(SlotProtocol.RequireString(a, "accountId", 64), SlotProtocol.GetString(a, "server", 64), ct).ConfigureAwait(false);
                return null;
            case SlotOps.Logout:
                await engine.LogoutAsync(ct).ConfigureAwait(false);
                return null;
            case SlotOps.JoinMap:
                await engine.JoinMapAsync(
                    ArmyNames.RequireMap(SlotProtocol.RequireString(a, "map", 64)),
                    ArmyNames.RequireCell(SlotProtocol.GetString(a, "cell", 64) ?? "Enter"),
                    ArmyNames.RequireCell(SlotProtocol.GetString(a, "pad", 64) ?? "Spawn"), ct).ConfigureAwait(false);
                return null;
            case SlotOps.GotoPlayer:
                {
                    string name = ArmyNames.RequirePlayer(SlotProtocol.RequireString(a, "name", 64));
                    if (!await engine.SendPacketAsync($"%xt%zm%cmd%1%goto%{name.ToLowerInvariant()}%", log: false, ct).ConfigureAwait(false))
                        throw new EngineException("The goto was not sent (not online).");
                    return null;
                }
            case SlotOps.LoadScript:
                engine.LoadScript(_host.ResolveScript(SlotProtocol.RequireString(a, "path", 1024)));
                return null;
            case SlotOps.StartScript:
                {
                    string path = _host.ResolveScript(SlotProtocol.RequireString(a, "path", 1024));
                    string? storage = SlotProtocol.GetString(a, "optionStorage", 200);
                    if (storage is not null)
                    {
                        ArmyNames.RequireOptionStorage(storage);
                        _host.PrepareOptionProfile(storage);
                    }
                    await engine.StartScriptAsync(path, new ScriptStartOptions(storage, SlotProtocol.GetBool(a, "silent", storage is not null)), ct).ConfigureAwait(false);
                    return null;
                }
            case SlotOps.StopScript:
                await engine.StopScriptAsync(ct).ConfigureAwait(false);
                return null;
            case SlotOps.Pause:
                await engine.PauseScriptAsync(ct).ConfigureAwait(false);
                return null;
            case SlotOps.Resume:
                await engine.ResumeScriptAsync(ct).ConfigureAwait(false);
                return null;
            case SlotOps.WorldLists:
                return SlotProtocol.ToNode(engine.GetWorldLists() with { Bank = [], Inventory = [], TempInventory = [] });
            case SlotOps.AcceptQuests:
                {
                    int[] ids = SlotProtocol.FromNode<int[]>(a?["ids"]) ?? [];
                    int sent = 0;
                    foreach (int id in ids.Where(i => i > 0).Distinct().Take(30))
                    {
                        if (await engine.SendPacketAsync($"%xt%zm%acceptQuest%1%{id}%", log: false, ct).ConfigureAwait(false))
                            sent++;
                    }
                    return JsonValue.Create(sent);
                }
            case SlotOps.AutoReconnect:
                engine.AutoReconnect = SlotProtocol.GetBool(a, "on", engine.AutoReconnect);
                return null;
            case SlotOps.Restart:
                if (!_host.RequestRestart(SlotProtocol.GetString(a, "reason", 200) ?? "restart requested by the coordinator"))
                    throw new EngineException("The slot cannot restart now (a script runs).");
                return null;
            default:
                throw new SlotProtocolException($"Unknown operation {m.Op}.");
        }
    }

    private SlotStatus Status(EngineSnapshot snapshot) => new(_host.Slot, _host.AccountId, snapshot, _host.QueueText);

    private static List<LogEntry> Trim(List<LogEntry> lines) =>
        lines.Select(l => l.Message.Length > 1000 ? l with { Message = l.Message[..1000] + "..." } : l).ToList();

    private void OnSnapshot(object? sender, EngineSnapshot snapshot)
    {
        lock (_gate)
            _pendingSnapshot = snapshot;
        ScheduleFlush();
    }

    private void OnLog(object? sender, LogEntry entry)
    {
        lock (_gate)
        {
            _pendingLog.Add(entry);
            if (_pendingLog.Count > SlotProtocol.MaxLogLines)
                _pendingLog.RemoveAt(0);
        }
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        lock (_gate)
        {
            if (_flushScheduled || _lifetime.IsCancellationRequested)
                return;
            _flushScheduled = true;
        }
        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        try
        {
            await Task.Delay(_eventInterval, _time, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Flush();
    }

    /// <summary>Sends what is pending now (also used by tests).</summary>
    public void Flush()
    {
        EngineSnapshot? snapshot;
        List<LogEntry> lines;
        lock (_gate)
        {
            _flushScheduled = false;
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            lines = _pendingLog.ToList();
            _pendingLog.Clear();
        }
        Action<byte[]>? handler = EventReady;
        if (handler is null)
            return;
        if (snapshot is not null)
            Raise(handler, new SlotMessage { Kind = SlotMessageKind.Event, Op = SlotEvents.Snapshot, Result = SlotProtocol.ToNode(Status(snapshot)) });
        if (lines.Count > 0)
            Raise(handler, new SlotMessage { Kind = SlotMessageKind.Event, Op = SlotEvents.Log, Result = SlotProtocol.ToNode(Trim(lines)) });
    }

    private static void Raise(Action<byte[]> handler, SlotMessage message)
    {
        byte[] bytes;
        try
        {
            bytes = SlotProtocol.Encode(message);
        }
        catch (SlotProtocolException)
        {
            return;
        }
        try
        {
            handler(bytes);
        }
        catch (Exception)
        {
            // A dead coordinator must never break the engine's event threads.
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _host.Engine.SnapshotChanged -= OnSnapshot;
        _host.Engine.LogAdded -= OnLog;
    }
}
