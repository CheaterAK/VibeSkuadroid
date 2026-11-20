using System.Text.Json.Nodes;

namespace Skua.Core.Headless.World;

// Access for the client layer (Client.GameClient) and the Flash bridge: atomic reads and writes of the stored tree.
public sealed partial class WorldModel
{
    /// <summary>Runs <paramref name="read"/> on the live tree under the model's lock. The delegate must not keep references.</summary>
    internal T Read<T>(Func<JsonObject, string?, T> read)
    {
        lock (_gate)
            return read(_world, _me);
    }

    /// <summary>Changes the live tree under the model's lock (client-side state the game keeps in its own objects).</summary>
    internal void Write(Action<JsonObject, string?> write, string reason = "client")
    {
        long version;
        lock (_gate)
        {
            write(_world, _me);
            _version++;
            version = _version;
        }
        Changed?.Invoke(this, new WorldChangedEventArgs(reason, true, version));
    }

    /// <summary>The quest slot values received this session (under the lock, no copy).</summary>
    internal IReadOnlyDictionary<int, int> QuestValueUpdatesUnsafe => _questValueUpdates;

    /// <summary>Accepted quest ids (under the lock, no copy).</summary>
    internal IReadOnlySet<int> AcceptedQuestsUnsafe => _acceptedQuests;

    /// <summary>
    /// Skua's rejectExcept: the client closes the drop windows of every offered drop whose name (case-insensitive) is
    /// not in <paramref name="keepNames"/>; they leave <see cref="GetDrops"/>. Nothing is sent (rejecting a drop is
    /// client-side only in the game too). Returns how many drops were removed.
    /// </summary>
    public int RejectDropsExcept(IEnumerable<string> keepNames)
    {
        HashSet<string> keep = new(keepNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        Write((_, _) =>
        {
            foreach (int id in _dropOrder.ToList())
            {
                string name = (JsonUtil.AsString(_drops[id].Raw["sName"]) ?? string.Empty).Trim();
                if (keep.Contains(name))
                    continue;
                _drops.Remove(id);
                _dropOrder.Remove(id);
                removed++;
            }
        }, "rejectExcept");
        return removed;
    }

    /// <summary>World.abandonQuest: the client forgets that the quest is in progress (no packet is sent).</summary>
    public void AbandonQuest(int questId)
    {
        Write((w, _) =>
        {
            _acceptedQuests.Remove(questId);
            if (((JsonObject)w["questTree"]!)[questId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject q)
                q["status"] = null;
        }, "abandonQuest");
    }

    /// <summary>
    /// Avatar.addItem from a script: the item joins the backpack (or the house inventory) locally, stacking onto one
    /// already held. Nothing is sent; the server's next inventory replaces it.
    /// </summary>
    public void AddItemLocally(JsonObject item)
    {
        Write((w, _) =>
        {
            JsonObject my = (JsonObject)w["myAvatar"]!;
            AddToStack(IsHouseItem(item) ? (JsonArray)my["houseitems"]! : (JsonArray)my["items"]!, item);
        }, "addItem");
    }

    /// <summary>True once the character's own data (initUserData) has arrived.</summary>
    public bool IsCharacterLoaded
    {
        get { lock (_gate) return _characterLoaded; }
    }

    /// <summary>Socket state as last reported with <see cref="SetConnected"/>.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
    }

    /// <summary>
    /// The Flash <c>setGameObject</c>: sets <paramref name="path"/> (dotted, indices allowed) to a copy of
    /// <paramref name="value"/> when its parent exists. Virtual members (<c>world.monsters</c>,
    /// <c>world.myAvatar.dataLeaf</c>, <c>sfc</c>, <c>headless</c>) cannot be written. Returns false when nothing was set.
    /// </summary>
    public bool SetGameObject(string path, JsonNode? value)
    {
        string[] parts = SplitPath(path);
        if (parts.Length < 2 || parts[0] != "world")
            return false;
        bool done = false;
        long version;
        lock (_gate)
        {
            JsonNode? parent = _world;
            for (int i = 1; i < parts.Length - 1 && parent is not null; i++)
            {
                string part = parts[i];
                if (ReferenceEquals(parent, _world) && part == "monsters")
                    return false;
                if (ReferenceEquals(parent, MyAvatar) && part == "dataLeaf")
                {
                    parent = MyLeaf;
                    continue;
                }
                parent = parent switch
                {
                    JsonObject o => o.TryGetPropertyValue(part, out JsonNode? next) ? next : null,
                    JsonArray a => int.TryParse(part, out int index) && index >= 0 && index < a.Count ? a[index] : null,
                    _ => null,
                };
            }
            string last = parts[^1];
            switch (parent)
            {
                case JsonObject o:
                    o[last] = value?.DeepClone();
                    done = true;
                    break;

                case JsonArray a when int.TryParse(last, out int index) && index >= 0:
                    while (a.Count <= index)
                        a.Add(null);
                    a[index] = value?.DeepClone();
                    done = true;
                    break;
            }
            if (!done)
                return false;
            _version++;
            version = _version;
        }
        Changed?.Invoke(this, new WorldChangedEventArgs("set:" + path, true, version));
        return true;
    }
}
