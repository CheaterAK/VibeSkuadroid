using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

// skua.swf's ExternalInterface callbacks (Skua.AS3 skua/Externalizer.as), with the return shapes of the AS3 sources.
public sealed partial class HeadlessFlashUtil
{
    private static string Bool(bool b) => b ? "true" : "false";

    private Dictionary<string, (BridgeSupport, Func<object?[], object?>)> BuildCallbacks()
    {
        Dictionary<string, (BridgeSupport, Func<object?[], object?>)> c = new(StringComparer.Ordinal);
        void Add(string name, Func<object?[], object?> f) => c[name] = (BridgeSupport.Implemented, f);
        void NoOp(string name, Func<object?[], object?> f) => c[name] = (BridgeSupport.NoOp, f);
        void Impossible(string name, string why) => c[name] = (BridgeSupport.Impossible, a =>
        {
            Gaps.Record(FlashGapKind.Impossible, name, a, why);
            return null;
        });

        // ---- Main.as: core and object access
        Add("loadClient", _ => LoadClient());
        NoOp("setBackgroundValues", _ => null);
        Add("isTrue", _ => "true");
        Add("isNull", a =>
        {
            Lookup l = Resolve(As3.Str(a, 0), recordGaps: false);
            return Bool(l.IsNullish);
        });
        Add("getGameObject", a =>
        {
            Lookup l = Resolve(As3.Str(a, 0));
            return As3.Stringify(l.Node, l.Defined);
        });
        Add("getGameObjectS", a =>
        {
            Lookup l = ResolveStatic(As3.Str(a, 0));
            return As3.Stringify(l.Node, l.Defined);
        });
        Add("getGameObjectKey", a =>
        {
            Lookup obj = Resolve(As3.Str(a, 0));
            if (obj.IsNullish)
                return null;   // AS3 TypeError on null[key]: the Windows bridge answers the default
            Lookup l = Walk(obj, new[] { As3.Str(a, 1) }, 0);
            return As3.Stringify(l.Node, l.Defined);
        });
        Add("setGameObject", a =>
        {
            Assign(As3.Str(a, 0), As3.ToNode(a.Length > 1 ? a[1] : null));
            return null;
        });
        Add("setGameObjectKey", a =>
        {
            Assign(As3.Str(a, 0) + "." + As3.Str(a, 1), As3.ToNode(a.Length > 2 ? a[2] : null));
            return null;
        });
        Add("getArrayObject", a =>
        {
            Lookup obj = Resolve(As3.Str(a, 0));
            if (obj.IsNullish)
                return null;
            Lookup l = Walk(obj, new[] { As3.Int(a, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }, 0);
            return As3.Stringify(l.Node, l.Defined);
        });
        Add("setArrayObject", a =>
        {
            Assign(As3.Str(a, 0) + "." + As3.Int(a, 1).ToString(System.Globalization.CultureInfo.InvariantCulture), As3.ToNode(a.Length > 2 ? a[2] : null));
            return null;
        });
        Add("callGameFunction", a => CallGameFunctionCore(As3.Str(a, 0), a.Skip(1).ToArray()));
        Add("callGameFunction0", a => CallGameFunctionCore(As3.Str(a, 0), Array.Empty<object?>()));
        Add("selectArrayObjects", a =>
        {
            Lookup obj = Resolve(As3.Str(a, 0));
            if (obj.Node is not JsonArray array)
                return string.Empty;
            string[] selector = As3.Str(a, 1).Split('.');
            JsonArray result = new();
            foreach (JsonNode? x in array)
                result.Add(Walk(Lookup.Of(x), selector, 0).Node?.DeepClone());
            return result.ToJsonString();
        });
        Add("sendClientPacket", a => SendClientPacket(As3.Str(a, 0), As3.Str(a, 1, "str")));
        Add("catchPackets", _ =>
        {
            Game.CatchingPackets = true;
            return null;
        });
        Impossible("injectScript", "loading and running SWF code needs Flash");

        // ---- Server.as
        Add("connectToServer", a =>
        {
            JsonObject? s = As3.JsonArg(a, 0);
            Net.GameServer? server = s is null ? null
                : Game.FindServer(JsonUtil.AsString(s["sName"]) ?? string.Empty) is { } byName && byName.Name.Equals(JsonUtil.AsString(s["sName"]), StringComparison.OrdinalIgnoreCase) ? byName
                : Game.FindServerByIp(JsonUtil.AsString(s["sIP"]) ?? string.Empty, JsonUtil.AsIntOrNull(s["iPort"]));
            if (server is not null)
                Game.ConnectTo(server);
            return "true";
        });
        Add("clickServer", a =>
        {
            Game.KillLoginModals();
            Net.GameServer? server = Game.FindServer(As3.Str(a, 0));
            return Bool(server is not null && Game.ConnectTo(server));
        });
        NoOp("loadPolicyFile", _ => null);
        NoOp("loadPolicyFiles", _ => null);
        Add("killLoginModals", _ =>
        {
            Game.KillLoginModals();
            return null;
        });
        Add("resetConnectionState", _ =>
        {
            Game.ResetConnectionState();
            return null;
        });
        Add("getActiveModalMessage", _ => Game.ModalMessage ?? string.Empty);

        // ---- Player.as
        Add("isLoggedIn", _ => Bool(Game.IsInGame));
        Add("isKicked", _ => Bool(Game.Kicked));
        Add("walkTo", a =>
        {
            Game.Client.WalkTo(As3.Int(a, 0), As3.Int(a, 1), As3.Int(a, 2, 8));
            return null;
        });
        Add("untargetSelf", _ =>
        {
            Game.Client.UntargetSelf();
            return null;
        });
        Add("attackPlayer", a => Bool(Game.Client.AttackPlayer(As3.Str(a, 0))));
        Add("getAvatar", a =>
        {
            int uid = As3.Int(a, 0);
            Lookup l = Resolve($"world.avatars.{uid}.objData");
            if (uid == Game.World.MyUserId)
                l = Resolve("world.myAvatar.objData");
            return As3.Stringify(l.Node, l.Defined);
        });
        // Player.getLoadouts: JSON.stringify(world.objInfo["customs"].loadouts). Before loadPrefs (no customs) AS3 throws
        // and the Windows bridge answers the default; a customs without loadouts stringifies to undefined.
        Add("getLoadouts", _ =>
        {
            if (!Game.IsInGame || !Game.World.HasLoadPrefs)
                return null;
            return Game.World.TryGetGameObjectNode("world.objInfo.customs.loadouts", out JsonNode? loadouts) ? As3.Stringify(loadouts) : null;
        });
        Add("gender", _ => $"\"{(JsonUtil.AsString(Game.World.GetPlayer()?.ObjData["strGender"]) ?? string.Empty).ToUpperInvariant()}\"");
        // Player.rejectExcept: every offered drop whose lower-case name is not in the comma-separated whitelist has its
        // drop window closed (client-side only, as in the game); here it leaves the model's drop list.
        Add("rejectExcept", a =>
        {
            Game.World.RejectDropsExcept(As3.Str(a, 0).Split(','));
            return "true";
        });

        // ---- World.as (skua)
        Add("jumpCorrectRoom", a =>
        {
            Game.Client.JumpCorrectRoom(As3.Str(a, 0), As3.Str(a, 1, "Spawn"), As3.Bool(a, 2, true), As3.Bool(a, 3));
            return null;
        });
        NoOp("disableDeathAd", a =>
        {
            Assign("userPreference.data.bDeathAd", JsonValue.Create(!As3.Bool(a, 0)));
            return null;
        });
        NoOp("skipCutscenes", _ => null);
        NoOp("hidePlayers", _ => null);
        NoOp("disableFX", _ => null);
        NoOp("killLag", a =>
        {
            lock (_treeGate)
                _worldExtra["visible"] = !As3.Bool(a, 0);
            return null;
        });

        // ---- Monster.as
        Add("availableMonsters", _ => Game.Client.MonstersJson(currentCellOnly: true).ToJsonString());
        Add("getMonsters", _ => Game.Client.MonstersJson(currentCellOnly: false).ToJsonString());
        Add("getTargetMonster", _ => Game.Client.TargetMonsterJson().ToJsonString());
        Add("attackMonsterName", a => Bool(Game.Client.AttackMonster(As3.Str(a, 0))));
        Add("attackMonsterID", a => Bool(Game.Client.AttackMonster(As3.Int(a, 0))));
        Add("selectMonsterName", a => Bool(Game.Client.SelectMonster(As3.Str(a, 0))));
        Add("selectMonsterID", a => Bool(Game.Client.SelectMonster(As3.Int(a, 0))));

        // ---- Skills.as
        Add("canUseSkill", a => Bool(Game.Client.CanUseSkill(As3.Int(a, 0))));
        Add("useSkill", a => Bool(Game.Client.UseSkill(As3.Int(a, 0))));

        // ---- Combat.as
        NoOp("infiniteRange", _ => null);   // ranges are not checked headless (no positions)
        NoOp("magnetize", _ => null);       // moves the target's clip; nothing is drawn

        // ---- Inventory.as (AQW 5.0 inventory pools)
        Add("hasInventoryCategories", _ => "true");
        Add("inventoryBagUsedSlots", _ => InventoryRules.CountBag(Game.Client.Character().Items));
        Add("inventoryMiscSlots", _ => Game.Client.MiscSlots);
        Add("inventoryMiscUsedSlots", _ => InventoryRules.CountMisc(Game.Client.Character().Items));
        Add("isFavoriteItem", _ => "false");   // favourites are a mark kept by the player's own game install (FavStore)
        Add("inventoryPool", a => As3.JsonArg(a, 0) is JsonObject item ? InventoryRules.PoolOf(item) : "bag");
        Add("inventoryHasSpaceFor", a => Bool(Game.Client.HasSpaceFor(As3.JsonArg(a, 0), As3.Int(a, 1, 1), As3.Bool(a, 2), As3.JsonArg(a, 3))));

        // ---- Shop.as
        Add("buyItemByName", a =>
        {
            ReportBuy(Game.Client.BuyItem(Game.Client.ShopItem(As3.Str(a, 0)), As3.Int(a, 1, -1)));
            return null;
        });
        Add("buyItemByID", a =>
        {
            ReportBuy(Game.Client.BuyItem(Game.Client.ShopItem(As3.Int(a, 0), As3.Int(a, 1, -1)), As3.Int(a, 2, -1)));
            return null;
        });
        Add("getShopItem", a => As3.Stringify(Game.Client.ShopItem(As3.Str(a, 0)), true));
        Add("getShopItemByID", a => As3.Stringify(Game.Client.ShopItem(As3.Int(a, 0), As3.Int(a, 1, -1)), true));
        Add("getUnmetPurchaseRequirements", a => new JsonArray(Game.Client.UnmetPurchaseRequirements(As3.Int(a, 0), As3.Int(a, 1, -1), As3.Int(a, 2, -1)).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()).ToJsonString());

        // ---- Auras.as
        Add("getSubjectAuras", a => SubjectAuras(As3.Str(a, 0)).ToJsonString());
        Add("GetAuraSnapshots", a => Game.Client.HudAuras.Snapshots(As3.Str(a, 0) == "Self" ? $"p:{Game.World.MyUserId}" : Game.Client.Target?.Subject, Game.Time.GetUtcNow().ToUnixTimeMilliseconds()).ToJsonString());
        Add("HasAnyActiveAura", a =>
        {
            HashSet<string> names = As3.Str(a, 1).Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
            return Bool(SubjectAuras(As3.Str(a, 0)).OfType<JsonObject>().Any(x => names.Contains((JsonUtil.AsString(x["nam"]) ?? string.Empty).ToLowerInvariant())));
        });
        Add("GetPlayerAura", a =>
        {
            Lookup leaf = Resolve($"world.uoTree.{As3.Str(a, 0).ToLowerInvariant()}", recordGaps: false);
            return leaf.Node is JsonObject o ? AuraRules.RebuildAuraArray(o["auras"] as JsonArray, NowMs).ToJsonString() : "[]";
        });
        Add("GetMonsterAuraByName", a =>
        {
            string name = As3.Str(a, 0).ToLowerInvariant();
            int id = 0;
            foreach (MonsterInfo m in Game.World.GetMonsters())
            {
                if (m.Name.ToLowerInvariant() == name)
                    id = m.MonMapId;
            }
            return MonsterAuras(id);
        });
        Add("GetMonsterAuraByID", a => MonsterAuras(As3.Int(a, 0)));
        Add("rebuildAuraArray", a => AuraRules.RebuildAuraArray(a.Length > 0 ? As3.ToNode(a[0]) as JsonArray : null, NowMs));
        Add("GetAurasValue", a =>
        {
            string name = As3.Str(a, 1).ToLowerInvariant();
            JsonObject? aura = SubjectAuras(As3.Str(a, 0)).OfType<JsonObject>().FirstOrDefault(x => (JsonUtil.AsString(x["nam"]) ?? string.Empty).ToLowerInvariant() == name);
            return aura is null ? 444 : As3.Number(JsonUtil.AsDoubleOrNull(aura["val"]) ?? 1);
        });
        Add("rebuilduoTree", a =>
        {
            Lookup leaf = Resolve($"world.uoTree.{As3.Str(a, 0).ToLowerInvariant()}", recordGaps: false);
            if (leaf.Node is not JsonObject o)
                return new JsonObject().ToJsonString();
            o["auras"] = AuraRules.RebuildAuraArray(o["auras"] as JsonArray, NowMs);
            return o.ToJsonString();
        });
        Add("rebuildmonTree", a =>
        {
            Lookup leaf = Resolve($"world.monTree.{As3.Int(a, 0)}", recordGaps: false);
            if (leaf.Node is not JsonObject o)
                return new JsonObject().ToJsonString();
            o["auras"] = AuraRules.RebuildAuraArray(o["auras"] as JsonArray, NowMs);
            return o.ToJsonString();
        });

        // ---- RemoteRegistry.as
        Add("lnkCreate", a => LinkCreate(As3.Str(a, 0)));
        Add("lnkDestroy", a =>
        {
            LinkDestroy(As3.Int(a, 0));
            return null;
        });
        Add("lnkGetChild", a => LinkChild(As3.Int(a, 0), As3.Str(a, 1)));
        Add("lnkDeleteChild", a =>
        {
            LinkDeleteChild(As3.Int(a, 0), As3.Str(a, 1));
            return null;
        });
        Add("lnkGetValue", a => LinkGetValue(As3.Int(a, 0)));
        Add("lnkSetValue", a =>
        {
            LinkSetValue(As3.Int(a, 0), a.Length > 1 ? a[1] : null);
            return null;
        });
        Add("lnkCall", a => LinkCall(As3.Int(a, 0), As3.Str(a, 1), a.Skip(2).ToArray()));
        Add("lnkGetArray", a => LinkChild(As3.Int(a, 0), As3.Int(a, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Add("lnkSetArray", a =>
        {
            int child = LinkChild(As3.Int(a, 0), As3.Int(a, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            LinkSetValue(child, a.Length > 2 ? a[2] : null);
            LinkDestroy(child);
            return null;
        });
        Add("fcCreate", a => FcCreate(As3.Int(a, 0), As3.Str(a, 1)));
        Add("fcPush", a =>
        {
            FcPushLinked(As3.Int(a, 0), a.Length > 1 ? a[1] : null);
            return null;
        });
        Add("fcPushDirect", a =>
        {
            FcPush(As3.Int(a, 0), a.Skip(1).ToArray());
            return null;
        });
        Add("fcClear", a =>
        {
            FcClear(As3.Int(a, 0));
            return null;
        });
        Add("fcCallFlash", a => FcCall(As3.Int(a, 0), asLink: true));
        Add("fcCall", a => FcCall(As3.Int(a, 0), asLink: false));

        // ---- Modules.as (display modules)
        NoOp("modEnable", a =>
        {
            lock (_treeGate)
                _modules.Add(As3.Str(a, 0));
            return null;
        });
        NoOp("modDisable", a =>
        {
            lock (_treeGate)
                _modules.Remove(As3.Str(a, 0));
            return null;
        });
        return c;
    }

    private long NowMs => Game.Time.GetUtcNow().ToUnixTimeMilliseconds();

    private void ReportBuy(string? refusal)
    {
        if (refusal is not null)
            Log?.Invoke($"Purchase not sent: {refusal}");
    }

    // Auras.getSubjectAuras: "Self" is this character's uoTree leaf, anything else the target monster's monTree leaf.
    private JsonArray SubjectAuras(string subject)
    {
        if (subject == "Self")
        {
            JsonObject? me = Game.World.GetPlayer()?.DataLeaf;
            return AuraRules.RebuildAuraArray(me?["auras"] as JsonArray, NowMs);
        }
        Client.TargetRef? t = Game.Client.Target;
        int monId = t is { Kind: 'm' } ? t.Id : 0;
        return AuraRules.RebuildAuraArray(Game.World.GetMonster(monId)?.Live?["auras"] as JsonArray, NowMs);
    }

    private string MonsterAuras(int monMapId)
    {
        JsonObject? leaf = Game.World.GetMonster(monMapId)?.Live;
        return leaf is null ? "Error: Couldn't get Monster Object Tree" : AuraRules.RebuildAuraArray(leaf["auras"] as JsonArray, NowMs).ToJsonString();
    }

    // Main.sendClientPacket: hands a packet to the game's own handler as if the server had sent it.
    private object? SendClientPacket(string packet, string type)
    {
        string text = type switch
        {
            "json" => packet,
            "xml" => packet,
            "str" => packet,
            _ => string.Empty,
        };
        if (text.Length == 0)
        {
            Log?.Invoke("sendClientPacket: invalid packet type " + type);
            return null;
        }
        Game.InjectInbound(text);
        return null;
    }
}
