using Skua.Core.Headless.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skua.Core.Headless.Tests.Fakes;

/// <summary>
/// A tiny in-process AQW game server on 127.0.0.1 for deterministic tests: the SmartFox XML handshake, %xt%
/// strings, JSON replies (large ones Z-compressed like the AQW 5.0 server) over a real loopback socket, with one
/// small world. C# port of Seraph's app/tests/fakeaqw.js (MIT, Ezzeldeen2006), extended with client version
/// rejection, token checks, a "slow down" rule, silence and drop switches.
/// </summary>
/// <remarks>
/// World: map testmap (cells Enter, r2 with two Pixies, r3 with a Pixie Queen), map shopmap; quest 1000
/// "Pixie Problem" (3x Pixie Dust, a temp drop from Pixies) and 1001; Pixie Wing as a normal drop (getDrop);
/// one bank item.
/// </remarks>
public sealed class FakeAqwServer : IAsyncDisposable
{
    public const string Token = "fake-token-123";

    internal static readonly Dictionary<int, JsonObject> Items = new()
    {
        [1] = Item(1, "Default Sword", "Sword", "Weapon", 1),
        [2] = Item(2, "Healer", "Class", "ar", 1),
        [10] = Item(10, "Pixie Dust", "Quest Item", "None", 10, temp: true),
        [11] = Item(11, "Pixie Wing", "Item", "None", 99),
        [12] = Item(12, "Pixie Crown", "Helm", "he", 1),
        [13] = Item(13, "Lost Ring", "Quest Item", "None", 1, temp: true),
        [30] = Item(30, "Old Boots", "Misc", "None", 1),
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Session> _sessions = new();
    private readonly Task _acceptTask;

    public FakeAqwServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Replies longer than this are sent compressed (AQW 5.0 behaviour). Null disables.</summary>
    public int? CompressAbove { get; set; } = 400;

    /// <summary>Logins with an older version are refused with the live server's message.</summary>
    public string? RequiredClientVersion { get; set; }

    /// <summary>Two game packets closer together than this get "Please slow down. Last action was too soon!".</summary>
    public TimeSpan? SlowDownBelow { get; set; }

    /// <summary>Stop answering anything (to test liveness detection).</summary>
    public bool Silent { get; set; }

    /// <summary>Set the AFK flag on the player right after the character loads.</summary>
    public bool MarkAfkAfterLoad { get; set; }

    public TimeSpan RespawnDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Cooldown of the auto attack in the skill bar (ms); shorter makes end-to-end fights quicker.</summary>
    public int AutoAttackCooldownMs { get; set; } = 2000;

    public GameServer Server => new("Sandbox", "127.0.0.1", Port, 1, 1000, true, false, 0, 0, "en", null);

    public IReadOnlyList<Session> Sessions => _sessions.ToArray();

    public Session? LastSession => _sessions.OrderByDescending(s => s.Number).FirstOrDefault();

    /// <summary>Every login nick seen (user~version), oldest first.</summary>
    public ConcurrentQueue<string> LoginNicks { get; } = new();

    private int _sessionCount;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }
            Session session = new(this, client, Interlocked.Increment(ref _sessionCount));
            _sessions.Add(session);
            _ = Task.Run(() => session.RunAsync(_cts.Token));
        }
    }

    /// <summary>Sends raw text (a NUL is appended) to every connected session.</summary>
    public void Broadcast(string raw)
    {
        foreach (Session s in _sessions)
            s.Raw(raw);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (Session s in _sessions)
            s.Close();
        try
        {
            await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
    }

    private static JsonObject Item(int id, string name, string type, string slot, int stack, bool temp = false)
    {
        return new JsonObject
        {
            ["ItemID"] = id,
            ["sName"] = name,
            ["sType"] = type,
            ["sES"] = slot,
            ["iStk"] = stack,
            ["bTemp"] = temp ? 1 : 0,
            ["bCoins"] = 0,
        };
    }

    internal static JsonObject Copy(int itemId)
    {
        return (JsonObject)Items[itemId].DeepClone();
    }

    /// <summary>One client connection, with the server-side state of its character.</summary>
    public sealed class Session
    {
        private readonly FakeAqwServer _server;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeGate = new();
        private readonly Dictionary<int, MonsterState> _monsters = new();
        private long _lastGamePacket;

        internal Session(FakeAqwServer server, TcpClient client, int number)
        {
            _server = server;
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            Number = number;
        }

        public int Number { get; }

        public int Uid { get; } = 40000 + Random.Shared.Next(1000);

        public string? Name { get; private set; }

        public string? ClientVersion { get; private set; }

        public int Room { get; private set; } = 900;

        public string Map { get; private set; } = string.Empty;

        public string Cell { get; private set; } = "Enter";

        public string Pad { get; private set; } = "Spawn";

        public int Gold { get; private set; } = 500;

        public bool Afk { get; private set; }

        public List<JsonObject> Inventory { get; } = new()
        {
            WithQty(Copy(1), 5000, 1, equip: true),
            WithQty(Copy(2), 5001, 3000, equip: true),
            WithQty(Copy(30), 5002, 1, equip: false),
        };

        public List<JsonObject> Temp { get; } = new();

        public List<JsonObject> Bank { get; } = new() { WithQty(Copy(11), 5003, 2, equip: false) };

        public HashSet<int> Accepted { get; } = new();

        public HashSet<int> PendingDrops { get; } = new();

        /// <summary>Every message received (handshake XML included), with a high-resolution timestamp.</summary>
        public ConcurrentQueue<(long Timestamp, string Text)> Received { get; } = new();

        /// <summary>Received %xt% commands in order.</summary>
        public IReadOnlyList<string> Commands => Received.Where(r => r.Text.StartsWith("%xt%", StringComparison.Ordinal)).Select(r => r.Text.Split('%')[3]).ToList();

        public bool Closed { get; private set; }

        private int _charItem = 6000;

        private static JsonObject WithQty(JsonObject item, int charItemId, int qty, bool equip)
        {
            item["CharItemID"] = charItemId;
            item["iQty"] = qty;
            item["bEquip"] = equip ? 1 : 0;
            return item;
        }

        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            List<byte> pending = new();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = await _stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] != 0)
                        {
                            pending.Add(buffer[i]);
                            continue;
                        }
                        if (pending.Count == 0)
                            continue;
                        string message = Encoding.UTF8.GetString(pending.ToArray());
                        pending.Clear();
                        Received.Enqueue((Stopwatch.GetTimestamp(), message));
                        if (!_server.Silent)
                            OnMessage(message);
                    }
                }
            }
            catch (Exception)
            {
                // Closed.
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            Closed = true;
            try { _client.Close(); } catch (Exception) { }
        }

        public void Raw(string text)
        {
            if (Closed)
                return;
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
            lock (_writeGate)
            {
                try
                {
                    _stream.Write(bytes);
                }
                catch (Exception)
                {
                    Closed = true;
                }
            }
        }

        public void Str(string cmd, params object[] args)
        {
            Raw($"%xt%{cmd}%-1%{string.Join('%', args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)))}%");
        }

        public void Json(JsonObject o)
        {
            string text = new JsonObject { ["t"] = "xt", ["b"] = new JsonObject { ["r"] = -1, ["o"] = o } }.ToJsonString();
            if (_server.CompressAbove is int limit && text.Length > limit)
            {
                using MemoryStream output = new();
                using (ZLibStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(Encoding.UTF8.GetBytes(text));
                text = "Z" + Convert.ToBase64String(output.ToArray());
            }
            Raw(text);
        }

        private void OnMessage(string m)
        {
            if (m.StartsWith('<'))
            {
                if (m.Contains("action='verChk'", StringComparison.Ordinal))
                {
                    Raw("<msg t='sys'><body action='apiOK' r='0'></body></msg>");
                }
                else if (m.Contains("action='login'", StringComparison.Ordinal))
                {
                    Login(m);
                }
                return;
            }
            if (!m.StartsWith("%xt%", StringComparison.Ordinal))
                return;

            string[] p = m.Split('%');
            string cmd = p[3];
            string[] a = p.Length > 6 ? p[5..^1] : Array.Empty<string>();

            long now = Stopwatch.GetTimestamp();
            if (_server.SlowDownBelow is TimeSpan minimum && _lastGamePacket != 0 && Stopwatch.GetElapsedTime(_lastGamePacket, now) < minimum)
                Str("warning", "Please slow down. Last action was too soon!");
            _lastGamePacket = now;

            switch (cmd)
            {
                case "firstJoin":
                    Join("testmap", "Enter", "Spawn");
                    return;

                case "cmd" when a.Length > 0 && a[0] == "tfer":
                    Join(a.Length > 2 ? a[2].Split('-')[0] : "testmap", a.Length > 3 ? a[3] : "Enter", a.Length > 4 ? a[4] : "Spawn");
                    return;

                case "cmd" when a.Length > 0 && a[0] == "logout":
                    Close();
                    return;

                case "moveToCell":
                    Cell = a[0];
                    Pad = a.Length > 1 ? a[1] : "Spawn";
                    Str("uotls", Name!.ToLowerInvariant(), $"strFrame:{Cell},strPad:{Pad},tx:0,ty:0");
                    return;

                case "retrieveUserData":
                case "retrieveUserDatas":
                    Json(new JsonObject { ["cmd"] = "initUserData", ["uid"] = Uid, ["data"] = UserData() });
                    return;

                case "retrieveInventory":
                    SendInventory();
                    if (_server.MarkAfkAfterLoad)
                    {
                        Afk = true;
                        Str("uotls", Name!.ToLowerInvariant(), "afk:true");
                    }
                    return;

                case "afk":
                    bool away = a.Length > 0 && a[0] == "true";
                    if (Afk != away)
                    {
                        Afk = away;
                        Str("uotls", Name!.ToLowerInvariant(), $"afk:{(away ? "true" : "false")}");
                    }
                    return;

                case "getQuests":
                    JsonObject quests = new();
                    foreach (string id in a)
                    {
                        if (int.TryParse(id, out int qid) && Quest(qid) is JsonObject q)
                            quests[id] = q;
                    }
                    Json(new JsonObject { ["cmd"] = "getQuests", ["quests"] = quests });
                    return;

                case "acceptQuest":
                    int acceptId = int.Parse(a[0], CultureInfo.InvariantCulture);
                    if (Quest(acceptId) is null)
                    {
                        Json(new JsonObject { ["cmd"] = "acceptQuest", ["QuestID"] = acceptId, ["bSuccess"] = 0 });
                        return;
                    }
                    Accepted.Add(acceptId);
                    Json(new JsonObject { ["cmd"] = "acceptQuest", ["QuestID"] = acceptId, ["bSuccess"] = 1 });
                    return;

                case "gar":
                    Attack(int.Parse(a[0], CultureInfo.InvariantCulture), a[1]);
                    return;

                case "restRequest":
                    Str("uotls", Name!.ToLowerInvariant(), "intHP:1000,intMP:100");
                    return;

                case "getDrop":
                    GetDrop(int.Parse(a[0], CultureInfo.InvariantCulture));
                    return;

                case "tryQuestComplete":
                    Complete(int.Parse(a[0], CultureInfo.InvariantCulture));
                    return;

                case "bankToInv" when a.Length >= 1:
                    MoveBetween(Bank, Inventory, int.Parse(a[0], CultureInfo.InvariantCulture), "bankToInv");
                    return;

                case "bankFromInv" when a.Length >= 1:
                    MoveBetween(Inventory, Bank, int.Parse(a[0], CultureInfo.InvariantCulture), "bankFromInv");
                    return;

                case "loadBank":
                    Json(new JsonObject { ["cmd"] = "loadBank", ["bitSuccess"] = 1, ["items"] = new JsonArray(Bank.Select(x => (JsonNode)x.DeepClone()).ToArray()) });
                    return;

                default:
                    return;
            }
        }

        // bankToInv / bankFromInv: the whole stack moves; the reply names the item.
        private void MoveBetween(List<JsonObject> from, List<JsonObject> to, int itemId, string cmd)
        {
            JsonObject? item = from.FirstOrDefault(x => (int)x["ItemID"]! == itemId);
            if (item is null)
            {
                Json(new JsonObject { ["cmd"] = cmd, ["ItemID"] = itemId, ["bSuccess"] = 0 });
                return;
            }
            from.Remove(item);
            item["bEquip"] = 0;
            to.Add(item);
            Json(new JsonObject { ["cmd"] = cmd, ["ItemID"] = itemId, ["bSuccess"] = 1 });
        }

        private void Login(string m)
        {
            Match nick = Regex.Match(m, "<nick><!\\[CDATA\\[([^\\]]+)\\]\\]></nick>");
            Match pword = Regex.Match(m, "<pword><!\\[CDATA\\[([^\\]]*)\\]\\]></pword>");
            string[] parts = nick.Groups[1].Value.Split('~');
            _server.LoginNicks.Enqueue(parts.Length >= 3 ? $"{parts[1]}~{parts[2]}" : nick.Groups[1].Value);
            Name = parts.Length > 1 ? parts[1] : "nobody";
            ClientVersion = parts.Length > 2 ? parts[2] : string.Empty;
            if (_server.RequiredClientVersion is string required && Older(ClientVersion, required))
            {
                Raw($"%xt%loginResponse%-1%0%-1%%Your game client is an older version. Server requires AQW game version {required} or higher. Please close the AQW game window and try again.%");
                return;
            }
            if (pword.Groups[1].Value != Token)
            {
                Raw("%xt%loginResponse%-1%0%-1%%Invalid login token.%");
                return;
            }
            Str("loginResponse", "true", Uid, Name.ToLowerInvariant(), "Welcome to the test server", string.Empty);
        }

        private static bool Older(string version, string required)
        {
            return !decimal.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v)
                || v < decimal.Parse(required, CultureInfo.InvariantCulture);
        }

        private JsonObject UserData()
        {
            return new JsonObject
            {
                ["strUsername"] = Name,
                ["intLevel"] = 30,
                ["intExp"] = 0,
                ["intExpToLevel"] = 10000,
                ["intGold"] = Gold,
                ["intCoins"] = 0,
                ["iUpgDays"] = -1,
                ["iBagSlots"] = 40,
                ["iBankSlots"] = 20,
                ["iHouseSlots"] = 20,
                ["strClassName"] = "Healer",
                ["iCP"] = 3000,
                ["CharID"] = 777,
                ["intAccessLevel"] = 1,
                ["strGender"] = "F",
                ["strQuests"] = new string('0', 100),
                ["strQuests2"] = new string('0', 100),
                ["eqp"] = new JsonObject(),
                ["intHP"] = 0,
                ["intHPMax"] = 1000,
                ["intMP"] = 100,
                ["intMPMax"] = 100,
            };
        }

        private void SendInventory()
        {
            Json(new JsonObject
            {
                ["cmd"] = "loadInventoryBig",
                ["items"] = new JsonArray(Inventory.Select(x => (JsonNode)x.DeepClone()).ToArray()),
                ["hitems"] = new JsonArray(),
                ["factions"] = new JsonArray(new JsonObject { ["FactionID"] = 2, ["sName"] = "Good", ["iRep"] = 1000 }),
                ["bankCount"] = Bank.Count,
                ["iMiscSlots"] = 100,
            });
            Json(new JsonObject
            {
                ["cmd"] = "sAct",
                ["actions"] = new JsonObject
                {
                    ["active"] = new JsonArray(
                        Skill("aa", "Auto attack", "aa", "h", _server.AutoAttackCooldownMs, 0, auto: true),
                        Skill("a1", "Heal", "m", "f", 3000, 10),
                        Skill("a2", "Smite", "m", "h", 4000, 15),
                        Skill("a3", "Ward", "m", "s", 6000, 20),
                        Skill("a4", "Judgement", "m", "h", 8000, 25)),
                    ["passive"] = new JsonArray(),
                },
            });
            Json(new JsonObject { ["cmd"] = "stu", ["sta"] = new JsonObject { ["$tha"] = 0.1, ["$cmc"] = 1, ["$ap"] = 50, ["$STR"] = 20 } });
        }

        private static JsonObject Skill(string skillRef, string name, string type, string target, int cd, int mp, bool auto = false)
        {
            return new JsonObject { ["ref"] = skillRef, ["nam"] = name, ["typ"] = type, ["tgt"] = target, ["cd"] = cd, ["mp"] = mp, ["range"] = 301, ["isOK"] = true, ["auto"] = auto };
        }

        private static JsonObject? Quest(int id)
        {
            return id switch
            {
                1000 => new JsonObject
                {
                    ["QuestID"] = 1000, ["sName"] = "Pixie Problem", ["iLvl"] = 1, ["bUpg"] = 0, ["bOnce"] = 0, ["iSlot"] = -1, ["iValue"] = 0,
                    ["iGold"] = 100, ["iExp"] = 50, ["oItems"] = new JsonObject { ["10"] = Copy(10) },
                    ["turnin"] = new JsonArray(new JsonObject { ["ItemID"] = 10, ["iQty"] = 3 }),
                },
                1001 => new JsonObject
                {
                    ["QuestID"] = 1001, ["sName"] = "Map item", ["iLvl"] = 1, ["bUpg"] = 0, ["bOnce"] = 0, ["iSlot"] = -1, ["iValue"] = 0,
                    ["iGold"] = 10, ["iExp"] = 10, ["turnin"] = new JsonArray(new JsonObject { ["ItemID"] = 13, ["iQty"] = 1 }),
                },
                _ => null,
            };
        }

        private void Join(string map, string cell, string pad)
        {
            (int monMapId, int monId, string frame)[] layout = map switch
            {
                "testmap" => new[] { (1, 5, "r2"), (2, 5, "r2"), (3, 6, "r3") },
                "shopmap" => Array.Empty<(int, int, string)>(),
                _ => null!,
            };
            if (layout is null)
            {
                Str("warning", $"\"{map}\" is not available.");
                return;
            }
            Map = map;
            Room++;
            Cell = cell;
            Pad = pad;
            _monsters.Clear();
            foreach ((int monMapId, int monId, string frame) in layout)
            {
                int hp = monId == 5 ? 100 : 400;
                _monsters[monMapId] = new MonsterState(monId, frame, hp);
            }

            JsonArray monBranch = new(layout.Select(m => (JsonNode)new JsonObject
            {
                ["MonMapID"] = m.monMapId, ["MonID"] = m.monId, ["intHP"] = _monsters[m.monMapId].Hp, ["intHPMax"] = _monsters[m.monMapId].MaxHp,
                ["intMP"] = 10, ["intMPMax"] = 10, ["intState"] = 1, ["iLvl"] = m.monId == 5 ? 3 : 10, ["wDPS"] = 5,
            }).ToArray());
            JsonArray monmap = new(layout.Select(m => (JsonNode)new JsonObject { ["MonMapID"] = m.monMapId, ["MonID"] = m.monId, ["strFrame"] = m.frame, ["bRed"] = 0 }).ToArray());
            JsonArray mondef = map == "testmap"
                ? new JsonArray(
                    new JsonObject { ["MonID"] = 5, ["strMonName"] = "Pixie", ["intLevel"] = 3, ["sRace"] = "Magical", ["strMonFileName"] = "pixie.swf" },
                    new JsonObject { ["MonID"] = 6, ["strMonName"] = "Pixie Queen", ["intLevel"] = 10, ["sRace"] = "Magical", ["strMonFileName"] = "queen.swf" })
                : new JsonArray();

            Json(new JsonObject
            {
                ["cmd"] = "moveToArea",
                ["areaId"] = Room,
                ["areaName"] = $"{map}-{Room}",
                ["strMapName"] = map,
                ["strMapFileName"] = $"test/{map}.swf",
                ["intType"] = 1,
                ["intKillCount"] = 0,
                ["uoBranch"] = new JsonArray(
                    new JsonObject
                    {
                        ["uoName"] = Name!.ToLowerInvariant(), ["strUsername"] = Name, ["entID"] = Uid, ["entType"] = "p",
                        ["intHP"] = 1000, ["intHPMax"] = 1000, ["intMP"] = 100, ["intMPMax"] = 100, ["intState"] = 1, ["intLevel"] = 30,
                        ["strFrame"] = cell, ["strPad"] = pad, ["tx"] = 0, ["ty"] = 0, ["afk"] = false,
                    },
                    new JsonObject
                    {
                        ["uoName"] = "otherplayer", ["strUsername"] = "OtherPlayer", ["entID"] = 41999, ["entType"] = "p",
                        ["intHP"] = "800", ["intHPMax"] = "800", ["intState"] = 1, ["intLevel"] = 12, ["strFrame"] = "Enter", ["strPad"] = "Spawn",
                    }),
                ["monBranch"] = monBranch,
                ["mondef"] = mondef,
                ["monmap"] = monmap,
            });
        }

        private void Attack(int actionId, string targets)
        {
            JsonObject m = new();
            JsonArray sarsa = new();
            List<int> killed = new();
            foreach (string t in targets.Split(','))
            {
                string[] rt = t.Split('>');
                string target = rt.Length > 1 ? rt[1] : string.Empty;
                if (!target.StartsWith("m:", StringComparison.Ordinal))
                    continue;
                int id = int.Parse(target[2..], CultureInfo.InvariantCulture);
                if (!_monsters.TryGetValue(id, out MonsterState? mon) || mon.State == 0 || mon.Cell != Cell)
                    continue;
                mon.Hp = Math.Max(0, mon.Hp - 50);
                mon.State = mon.Hp == 0 ? 0 : 2;
                m[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["intHP"] = mon.Hp, ["intState"] = mon.State };
                sarsa.Add(new JsonObject
                {
                    ["cInf"] = $"p:{Uid}", ["actID"] = actionId, ["iRes"] = 1,
                    ["a"] = new JsonArray(new JsonObject { ["tInf"] = $"m:{id}", ["type"] = "hit", ["hp"] = 50 }),
                });
                if (mon.State == 0)
                    killed.Add(id);
            }

            JsonArray auras = new();
            if (m.Count > 0)
            {
                string first = m.First().Key;
                auras.Add(new JsonObject
                {
                    ["cmd"] = "aura+", ["cInf"] = $"p:{Uid}", ["tInf"] = $"m:{first}",
                    ["auras"] = new JsonArray(new JsonObject { ["nam"] = "Judged", ["t"] = "d", ["dur"] = 5, ["val"] = 0.1, ["isNew"] = true }),
                });
            }

            Json(new JsonObject
            {
                ["cmd"] = "ct",
                ["m"] = m,
                ["p"] = new JsonObject { [Name!.ToLowerInvariant()] = new JsonObject { ["intState"] = m.Count > 0 ? 2 : 1, ["intHP"] = 980 } },
                ["a"] = auras,
                ["sarsa"] = sarsa,
            });
            foreach (int id in killed)
                OnKill(id);
        }

        private void OnKill(int id)
        {
            Gold += 5;
            Json(new JsonObject { ["cmd"] = "addGoldExp", ["typ"] = "m", ["id"] = id, ["intExp"] = 10, ["intGold"] = 5 });
            Str("uotls", Name!.ToLowerInvariant(), "intState:1");
            if (Accepted.Contains(1000))
                GiveTemp(10, 1);
            PendingDrops.Add(11);
            JsonObject drop = Copy(11);
            drop["iQty"] = 1;
            Json(new JsonObject { ["cmd"] = "dropItem", ["items"] = new JsonObject { ["11"] = drop } });
            _ = Task.Delay(_server.RespawnDelay).ContinueWith(_ =>
            {
                if (!_monsters.TryGetValue(id, out MonsterState? mon))
                    return;
                mon.Hp = mon.MaxHp;
                mon.State = 1;
                Str("respawnMon", id);
                Str("mtls", id, $"intHP:{mon.MaxHp},intState:1");
            }, TaskScheduler.Default);
        }

        private void GiveTemp(int id, int qty)
        {
            JsonObject? t = Temp.FirstOrDefault(x => (int)x["ItemID"]! == id);
            int max = (int)Items[id]["iStk"]!;
            if (t is null)
            {
                t = Copy(id);
                t["iQty"] = 0;
                Temp.Add(t);
            }
            t["iQty"] = Math.Min(max, (int)t["iQty"]! + qty);
            JsonObject grant = Copy(id);
            grant["iQty"] = qty;
            grant["iQtyNow"] = (int)t["iQty"]!;
            grant["bTemp"] = 1;
            Json(new JsonObject { ["cmd"] = "addItems", ["items"] = new JsonObject { [id.ToString(CultureInfo.InvariantCulture)] = grant } });
        }

        private void GetDrop(int id)
        {
            if (!PendingDrops.Remove(id))
            {
                Json(new JsonObject { ["cmd"] = "getDrop", ["bSuccess"] = 0, ["ItemID"] = id });
                return;
            }
            JsonObject? inv = Inventory.FirstOrDefault(x => (int)x["ItemID"]! == id);
            int charItemId;
            if (inv is not null)
            {
                inv["iQty"] = (int)inv["iQty"]! + 1;
                charItemId = (int)inv["CharItemID"]!;
            }
            else
            {
                charItemId = _charItem++;
                Inventory.Add(WithQty(Copy(id), charItemId, 1, equip: false));
            }
            Json(new JsonObject
            {
                ["cmd"] = "getDrop", ["bSuccess"] = 1, ["ItemID"] = id, ["CharItemID"] = charItemId, ["iQty"] = 1,
                ["iQtyNow"] = inv is null ? 1 : (int)inv["iQty"]!, ["bBank"] = false,
            });
        }

        private void Complete(int questId)
        {
            JsonObject? q = Quest(questId);
            if (q is null || !Accepted.Contains(questId))
            {
                Json(new JsonObject { ["cmd"] = "ccqr", ["QuestID"] = questId, ["bSuccess"] = 0, ["msg"] = "not accepted" });
                return;
            }
            foreach (JsonObject t in ((JsonArray)q["turnin"]!).Cast<JsonObject>())
            {
                int id = (int)t["ItemID"]!;
                int qty = (int)t["iQty"]!;
                JsonObject? have = Temp.FirstOrDefault(x => (int)x["ItemID"]! == id) ?? Inventory.FirstOrDefault(x => (int)x["ItemID"]! == id);
                if (have is null || (int)have["iQty"]! < qty)
                {
                    Json(new JsonObject { ["cmd"] = "ccqr", ["QuestID"] = questId, ["bSuccess"] = 0, ["msg"] = "Missing turn in item." });
                    return;
                }
            }
            string sItems = string.Join(',', ((JsonArray)q["turnin"]!).Cast<JsonObject>().Select(t => $"{t["ItemID"]}:{t["iQty"]}"));
            foreach (JsonObject t in ((JsonArray)q["turnin"]!).Cast<JsonObject>())
            {
                JsonObject? temp = Temp.FirstOrDefault(x => (int)x["ItemID"]! == (int)t["ItemID"]!);
                if (temp is null)
                    continue;
                temp["iQty"] = (int)temp["iQty"]! - (int)t["iQty"]!;
                if ((int)temp["iQty"]! < 1)
                    Temp.Remove(temp);
            }
            Accepted.Remove(questId);
            int gold = (int)q["iGold"]!;
            Gold += gold;
            Json(new JsonObject { ["cmd"] = "turnIn", ["sItems"] = sItems });
            Json(new JsonObject { ["cmd"] = "ccqr", ["QuestID"] = questId, ["bSuccess"] = 1, ["sName"] = (string)q["sName"]!, ["rewardObj"] = new JsonObject { ["intGold"] = gold, ["intExp"] = (int)q["iExp"]! } });
            Json(new JsonObject { ["cmd"] = "addGoldExp", ["typ"] = "q", ["intGold"] = gold, ["intExp"] = (int)q["iExp"]! });
            if (questId == 1000 && Inventory.All(x => (int)x["ItemID"]! != 12))
            {
                int charItemId = _charItem++;
                Inventory.Add(WithQty(Copy(12), charItemId, 1, equip: false));
                JsonObject reward = Copy(12);
                reward["CharItemID"] = charItemId;
                reward["iQty"] = 1;
                reward["iQtyNow"] = 1;
                Json(new JsonObject { ["cmd"] = "addItems", ["items"] = new JsonObject { ["12"] = reward } });
            }
        }

        private sealed class MonsterState
        {
            public MonsterState(int monId, string cell, int hp)
            {
                MonId = monId;
                Cell = cell;
                Hp = hp;
                MaxHp = hp;
                State = 1;
            }

            public int MonId { get; }

            public string Cell { get; }

            public int Hp { get; set; }

            public int MaxHp { get; }

            public int State { get; set; }
        }
    }
}
