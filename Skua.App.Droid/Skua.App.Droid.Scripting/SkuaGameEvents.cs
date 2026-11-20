using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Notifications;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models.Items;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// <see cref="IGameEvents"/> on the Skua session: Skua.Core's game and script messages (what the desktop's Notification
/// Center, Notify Drop and DiscordWebhookService listen to) as plain <see cref="GameEvent"/>s, the desktop Current
/// Drops window's actions on Skua's <see cref="IScriptDrop"/>, and Skua's <see cref="IJunkService"/> (the desktop's
/// JunkItems.json) with automatic rejection of junk drops.
/// </summary>
/// <remarks>
/// Message handlers run on the thread that received the packet: they only raise the event and, for a junk drop,
/// queue its rejection. Drop actions run one at a time on a background task (Skua's pickup and AC handling wait for
/// the server's replies).
/// </remarks>
internal sealed class SkuaGameEvents : IGameEvents, IJunkList, IDisposable
{
    private const int RememberedDrops = 256;

    private readonly Func<IScriptInterface?> _bot;
    private readonly Func<IServiceProvider?> _services;
    private readonly Action<string> _warn;
    private readonly object _recipient = new();
    private readonly ConcurrentDictionary<int, string> _dropNames = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private volatile bool _autoReject;
    private volatile Func<string, bool>? _keep;
    private bool _disposed;

    public SkuaGameEvents(Func<IScriptInterface?> bot, Func<IServiceProvider?> services, Action<string> warn)
    {
        _bot = bot;
        _services = services;
        _warn = warn;
        StrongReferenceMessenger m = StrongReferenceMessenger.Default;
        int game = (int)MessageChannels.GameEvents;
        int status = (int)MessageChannels.ScriptStatus;
        m.Register<object, ItemDroppedMessage, int>(_recipient, game, (_, msg) => OnItemDropped(msg));
        m.Register<object, DropPickupResultMessage, int>(_recipient, game, (_, msg) => OnPickupResult(msg));
        m.Register<object, ItemSoldMessage, int>(_recipient, game, (_, msg) =>
            Raise(new GameEvent(GameEventKind.ItemSold, Now) { Quantity = msg.QuantitySold, Amount = msg.Cost, Flag = msg.IsAC }));
        m.Register<object, PlayerDeathMessage, int>(_recipient, game, (_, _) => Raise(new GameEvent(GameEventKind.PlayerDied, Now)));
        m.Register<object, ReloginTriggeredMessage, int>(_recipient, game, (_, msg) => Raise(new GameEvent(GameEventKind.Relogin, Now) { Flag = msg.WasKicked }));
        m.Register<object, MapChangedMessage, int>(_recipient, game, (_, msg) => Raise(new GameEvent(GameEventKind.MapChanged, Now) { Name = msg.Map }));
        m.Register<object, QuestAcceptedMessage, int>(_recipient, game, (_, msg) => Raise(new GameEvent(GameEventKind.QuestAccepted, Now) { Id = msg.QuestID }));
        m.Register<object, QuestTurninMessage, int>(_recipient, game, (_, msg) => Raise(new GameEvent(GameEventKind.QuestCompleted, Now) { Id = msg.QuestID }));
        m.Register<object, ScriptStartedMessage, int>(_recipient, status, (_, _) => Raise(new GameEvent(GameEventKind.ScriptStarted, Now) { Name = ScriptName() }));
        m.Register<object, ScriptStoppedMessage, int>(_recipient, status, (_, _) => Raise(new GameEvent(GameEventKind.ScriptStopped, Now) { Name = ScriptName() }));
        m.Register<object, ScriptErrorMessage, int>(_recipient, status, (_, msg) =>
        {
            Exception e = msg.Exception is System.Reflection.TargetInvocationException { InnerException: not null } t ? t.InnerException! : msg.Exception;
            Raise(new GameEvent(GameEventKind.ScriptError, Now) { Name = ScriptName(), Detail = $"{e.GetType().Name}: {e.Message}" });
        });
    }

    public event EventHandler<GameEvent>? Raised;

    public IJunkList Junk => this;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ------------------------------------------------------------------ events

    private void OnItemDropped(ItemDroppedMessage msg)
    {
        ItemBase item = msg.Item;
        if (item is null)
            return;
        string name = item.Name?.Trim() ?? string.Empty;
        if (msg.AddedToInv)
        {
            Raise(new GameEvent(GameEventKind.ItemObtained, Now) { Name = name, Id = item.ID });
            return;
        }
        if (_dropNames.Count > RememberedDrops)
            _dropNames.Clear();
        _dropNames[item.ID] = name;
        Raise(new GameEvent(GameEventKind.ItemDropped, Now) { Name = name, Id = item.ID, Quantity = item.Quantity });
        if (_autoReject && IsJunk(item.ID) && _keep?.Invoke(name) != true)
            Queue(() => RejectJunkDrop(item.ID, name));
    }

    private void OnPickupResult(DropPickupResultMessage msg)
    {
        if (msg.Success && _dropNames.TryRemove(msg.ItemID, out string? name))
            Raise(new GameEvent(GameEventKind.ItemObtained, Now) { Name = name, Id = msg.ItemID, Quantity = 1 });
    }

    private void Raise(GameEvent e)
    {
        try
        {
            Raised?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            // A listener's failure must never reach Skua's packet handling.
            _warn($"Game event listener failed: {ex.GetType().Name}");
        }
    }

    private string? ScriptName()
    {
        string? path = _bot()?.Manager?.LoadedScript;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
    }

    // ------------------------------------------------------------------ drop actions

    public void PickupDrops(IReadOnlyCollection<int> itemIds)
    {
        int[] ids = itemIds.ToArray();
        if (ids.Length > 0)
            Queue(() => _bot()?.Drops.Pickup(ids));
    }

    public void PickupAllDrops() => Queue(() => _bot()?.Drops.PickupAll());

    public void RejectDrops(IReadOnlyCollection<int> itemIds)
    {
        HashSet<int> ids = itemIds.ToHashSet();
        if (ids.Count > 0)
            Queue(() => Reject(ids));
    }

    /// <summary>Rejects the drops with these ids: Skua's RejectExcept with every other offered drop kept.</summary>
    private void Reject(HashSet<int> ids)
    {
        IScriptDrop? drops = _bot()?.Drops;
        if (drops is null)
            return;
        string[] keep = drops.CurrentDropInfos.Where(d => !ids.Contains(d.ID)).Select(d => d.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        drops.RejectExcept(keep);
    }

    private void RejectJunkDrop(int id, string name)
    {
        IScriptDrop? drops = _bot()?.Drops;
        if (drops is null || !_autoReject)
            return;
        // The running script asked for this item: it wins over the junk list.
        if (drops.ToPickupIDs.Contains(id) || drops.ToPickup.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return;
        if (!drops.CurrentDropInfos.Any(d => d.ID == id))
            return; // picked up or gone already
        Reject([id]);
        Raise(new GameEvent(GameEventKind.DropRejected, Now) { Name = name, Id = id });
    }

    private void Queue(Action action)
    {
        if (_disposed)
            return;
        _ = Task.Run(async () =>
        {
            await _actions.WaitAsync().ConfigureAwait(false);
            try
            {
                action();
            }
            catch (Exception e)
            {
                _warn($"Drop action failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _actions.Release();
            }
        });
    }

    // ------------------------------------------------------------------ junk

    private IJunkService? JunkService => _services()?.GetService<IJunkService>();

    public IReadOnlyList<JunkItem> Items =>
        JunkService?.JunkItems.Select(j => new JunkItem(j.ID, j.Name, j.Category)).ToList() ?? [];

    public bool IsJunk(int itemId) => JunkService?.IsJunk(itemId) ?? false;

    public void Set(IEnumerable<JunkItem> items)
    {
        IJunkService service = JunkService ?? throw new InvalidOperationException("The Skua session is not available.");
        service.SetJunk(items.Select(i => new JunkItemConfig { ID = i.Id, Name = i.Name, Category = i.Category, Meta = string.Empty }));
    }

    public void Reload() => JunkService?.Load();

    public int SellAll()
    {
        IJunkService service = JunkService ?? throw new InvalidOperationException("The Skua session is not available.");
        IScriptInterface bot = _bot() ?? throw new InvalidOperationException("The Skua session is not available.");
        HashSet<int> junk = service.JunkItems.Select(j => j.ID).ToHashSet();
        int offered = bot.Inventory.Items.Count(i => junk.Contains(i.ID) && !i.Equipped);
        service.SellAllJunk();
        return offered;
    }

    public bool AutoReject
    {
        get => _autoReject;
        set => _autoReject = value;
    }

    public Func<string, bool>? Keep
    {
        get => _keep;
        set => _keep = value;
    }

    public void Dispose()
    {
        _disposed = true;
        StrongReferenceMessenger.Default.UnregisterAll(_recipient);
    }
}
