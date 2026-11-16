namespace Spike.HostApi;

/// <summary>
/// Stand-in for Skua's IScriptInterface: implemented by the host app, handed to compiled scripts.
/// </summary>
public interface IScriptHost
{
    string PlayerName { get; }
    int MapId { get; }
    bool ShouldStop { get; }
    void Log(string message);
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    Task DelayAsync(int milliseconds, CancellationToken ct = default);
    event EventHandler<HostEventArgs>? HostEvent;
    void RaiseHostEvent(string name);
    IReadOnlyList<InventoryItem> Inventory { get; }
}

public sealed record InventoryItem(int Id, string Name, int Quantity, ItemCategory Category);

public enum ItemCategory { Misc, Weapon, Armor, Quest, Resource }

public sealed class HostEventArgs(string name) : EventArgs
{
    public string Name { get; } = name;
}

/// <summary>Implemented by scripts, consumed by the host (tests the reverse direction).</summary>
public interface IScriptPlugin
{
    string Name { get; }
    int Execute(IScriptHost host, int input);
}
