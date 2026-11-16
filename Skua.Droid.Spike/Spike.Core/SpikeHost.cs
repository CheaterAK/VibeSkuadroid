using System.Collections.Concurrent;
using Spike.HostApi;

namespace Spike.Core;

/// <summary>Host-side implementation of the script API.</summary>
public sealed class SpikeHost(Action<string> log) : IScriptHost
{
    readonly ConcurrentDictionary<string, object?> _store = new();
    public volatile bool StopRequested;
    public int LogCalls;

    public string PlayerName => "SpikePlayer";
    public int MapId => 1337;
    public bool ShouldStop => StopRequested;

    public void Log(string message)
    {
        Interlocked.Increment(ref LogCalls);
        log($"    [script@thread {Environment.CurrentManagedThreadId}] {message}");
    }

    public T Get<T>(string key, T fallback) => _store.TryGetValue(key, out var v) && v is T t ? t : fallback;
    public void Set<T>(string key, T value) => _store[key] = value;
    public Task DelayAsync(int milliseconds, CancellationToken ct = default) => Task.Delay(milliseconds, ct);

    public event EventHandler<HostEventArgs>? HostEvent;
    public void RaiseHostEvent(string name) => HostEvent?.Invoke(this, new HostEventArgs(name));

    public IReadOnlyList<InventoryItem> Inventory { get; } =
        Enumerable.Range(1, 40)
            .Select(i => new InventoryItem(i, $"Item{i}", i * 3 % 17 + 1, (ItemCategory)(i % 5)))
            .ToArray();
}
