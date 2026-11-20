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
/// <para>
/// Login rules: the password is the <c>sToken</c> of the account's latest web login. Each web login a test serves
/// through <see cref="IssueToken"/> supersedes the account's earlier tokens, and a socket login with a superseded
/// token, or with a client version listed in <see cref="RefusedClientVersions"/>, is refused with the live server's
/// "Character data could not be retrieved" loginResponse. The nick's name must be the token's account in any case
/// (the game sends it lowercased, another client that gets in sends <c>unm</c> as it is). <see cref="Token"/> is the
/// account's token until the first <see cref="IssueToken"/>.
/// </para>
/// </remarks>
/// <remarks>
/// World: map testmap (cells Enter, r2 with two Pixies, r3 with a Pixie Queen), map shopmap; quest 1000
/// "Pixie Problem" (3x Pixie Dust, a temp drop from Pixies; rewards gold, XP and a Pixie Crown) and 1001; Pixie Wing
/// as a normal drop (getDrop); one bank item; shop 77 "Pixie Shop" (a potion for gold, a hat the character cannot
/// afford, a member cape, a charm merged from Pixie Wings) with buyItem and sellItem.
/// </remarks>
public sealed class FakeAqwServer : IAsyncDisposable
{
    public const string Token = "fake-token-123";

    /// <summary>Every token this fake ever hands out starts with this (so tests can check none leaks).</summary>
    public const string TokenPrefix = "fake-token-";

    /// <summary>The account <see cref="Token"/> belongs to.</summary>
    public const string Account = "Hero";

    /// <summary>The live server's refusal seen on 2026-11-19 (the third field of the loginResponse is 0, the sixth this text).</summary>
    public const string CharacterDataRefusal = "Character data could not be retrieved. Please Login and try again.";

    internal static readonly Dictionary<int, JsonObject> Items = new()
    {
        [1] = Item(1, "Default Sword", "Sword", "Weapon", 1),
        [2] = Item(2, "Healer", "Class", "ar", 1),
        [10] = Item(10, "Pixie Dust", "Quest Item", "None", 10, temp: true),
        [11] = Item(11, "Pixie Wing", "Item", "None", 99),
        [12] = Item(12, "Pixie Crown", "Helm", "he", 1),
        [13] = Item(13, "Lost Ring", "Quest Item", "None", 1, temp: true),
        [30] = Item(30, "Old Boots", "Misc", "None", 1),
        [40] = Item(40, "Health Potion", "Item", "None", 99),
        [41] = Item(41, "Fancy Hat", "Helm", "he", 1),
        [42] = Item(42, "Member Cape", "Cape", "ba", 1),
        [43] = Item(43, "Wing Charm", "Item", "None", 10),
        [50] = Item(50, "Rogue", "Class", "ar", 1),
        [51] = Item(51, "Pixie Blade", "Sword", "Weapon", 1),
        [52] = Item(52, "Pixie Robe", "Armor", "co", 1),
    };

    /// <summary>Shop 77: (ItemID, ShopItemID, cost in gold, member only, merge material id and quantity or 0).</summary>
    internal static readonly (int ItemId, int ShopItemId, int Cost, bool Member, int MaterialId, int MaterialQty)[] ShopStock =
    {
        (40, 9001, 10, false, 0, 0),
        (41, 9002, 1000, false, 0, 0),
        (42, 9003, 50, true, 0, 0),
        (43, 9004, 0, false, 11, 2),
    };

    /// <summary>The shop the fake serves.</summary>
    public const int ShopId = 77;

    internal static JsonObject ShopItemJson((int ItemId, int ShopItemId, int Cost, bool Member, int MaterialId, int MaterialQty) s)
    {
        JsonObject item = Copy(s.ItemId);
        item["ShopItemID"] = s.ShopItemId;
        item["iCost"] = s.Cost;
        item["iQty"] = 1;
        item["bUpg"] = s.Member ? 1 : 0;
        if (s.MaterialId > 0)
            item["turnin"] = new JsonArray(new JsonObject { ["ItemID"] = s.MaterialId, ["iQty"] = s.MaterialQty, ["sName"] = (string)Items[s.MaterialId]["sName"]! });
        return item;
    }

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

    public GameServer Server => new(Name, "127.0.0.1", Port, 1, 1000, true, false, 0, 0, "en", null);

    public IReadOnlyList<Session> Sessions => _sessions.ToArray();

    public Session? LastSession => _sessions.OrderByDescending(s => s.Number).FirstOrDefault();

    /// <summary>Every login nick seen (user~version), oldest first.</summary>
    public ConcurrentQueue<string> LoginNicks { get; } = new();

    /// <summary>Every verChk API version seen, oldest first.</summary>
    public ConcurrentQueue<string> ApiVersions { get; } = new();

    /// <summary>verChk with another version is answered with apiKO. Null accepts any (as the live server did for 166).</summary>
    public string? RequiredApiVersion { get; set; }

    /// <summary>
    /// Client versions (as written in the nick) refused with <see cref="CharacterDataRefusal"/>, the way the live
    /// server refused 5.33 while it let a client with 5.32 in.
    /// </summary>
    public HashSet<string> RefusedClientVersions { get; } = new(StringComparer.Ordinal);

    /// <summary>Every token <see cref="IssueToken"/> handed out, oldest first.</summary>
    public ConcurrentQueue<string> IssuedTokens { get; } = new();

    /// <summary>The message field of a successful loginResponse.</summary>
    public string LoginMessage { get; set; } = "Welcome to the test server";

    /// <summary>Every socket login's outcome, oldest first: the token it used and why it was refused (null when accepted).</summary>
    public ConcurrentQueue<(string Token, string? Refusal)> LoginOutcomes { get; } = new();

    private readonly object _tokenGate = new();
    private readonly Dictionary<string, (string Account, bool Current, long IssuedAt)> _tokens = new(StringComparer.Ordinal)
    {
        [Token] = (Account, true, 0),
    };
    private int _tokenCount;

    /// <summary>
    /// A web login for <paramref name="account"/> (the <c>unm</c> the test's HTTP fake returns): a fresh token that
    /// supersedes the account's earlier ones, as the live web login does.
    /// </summary>
    public string IssueToken(string account = Account)
    {
        lock (_tokenGate)
        {
            foreach (string key in _tokens.Keys.ToList())
            {
                (string owner, bool current, long at) = _tokens[key];
                if (current && owner.Equals(account, StringComparison.OrdinalIgnoreCase))
                    _tokens[key] = (owner, false, at);
            }
            string token = $"{TokenPrefix}r{Interlocked.Increment(ref _tokenCount)}-{Random.Shared.Next(100000, 999999)}";
            _tokens[token] = (account, true, Stopwatch.GetTimestamp());
            IssuedTokens.Enqueue(token);
            return token;
        }
    }

    /// <summary>Why a socket login with this nick name and token is refused, or null with the account's display name.</summary>
    /// <summary>
    /// Another fake whose web logins this one accepts (the live game servers share one account database): a token
    /// <see cref="Accounts"/> issued logs in here too. Null: this fake's own tokens.
    /// </summary>
    public FakeAqwServer? Accounts { get; init; }

    /// <summary>The name this fake has in the server lists a test builds (<see cref="Server"/>).</summary>
    public string Name { get; init; } = "Sandbox";

    private (string? Refusal, string? Account) CheckLogin(string nickName, string token)
    {
        if (Accounts is not null)
            return Accounts.CheckLogin(nickName, token);
        lock (_tokenGate)
        {
            if (!_tokens.TryGetValue(token, out (string Account, bool Current, long IssuedAt) t))
                return ("Invalid login token.", null);
            if (!t.Current)
                return (CharacterDataRefusal, null);
            if (!nickName.Equals(t.Account, StringComparison.OrdinalIgnoreCase))
                return (CharacterDataRefusal, null);
            return (null, t.Account);
        }
    }

    /// <summary>
    /// Answer the socket <c>loadBank</c> request (the game defines the request and its reply handler but never
    /// sends it; whether the live server still answers it is unknown). False: no reply, as suspected live.
    /// </summary>
    public bool AnswerSocketLoadBank { get; set; } = true;

    /// <summary>
    /// The account's saved outfits (<c>customs.loadouts</c>), sent in loadPrefs right after the login as the live server
    /// does (<c>{"costumes":{},"loadouts":{},"prefs":{}}</c> for an account without any). Shape (OutfitSets interface):
    /// set name to <c>{he, ba, ar, co, Weapon, pe, am, mi: ItemID, colors: {hair, skin, eye, base, trim, accessory}}</c>.
    /// </summary>
    public JsonObject Loadouts { get; } = new();

    /// <summary>The character's outfit slots (objData.iLoadoutSlots; 20 on a free account).</summary>
    public int LoadoutSlots { get; set; } = 20;

    /// <summary>Items every new session's backpack starts with besides the default ones (unequipped).</summary>
    public List<int> ExtraInventory { get; } = new();

    /// <summary>
    /// Every outfit request (equipLoadout, wearLoadout, addLoadout, removeLoadout) as the server read it, oldest first:
    /// the command, its fields after the room, and why it broke the game client's request contract (null when it met it).
    /// </summary>
    public ConcurrentQueue<(string Command, string[] Args, string? Violation)> LoadoutRequests { get; } = new();

    /// <summary>HTTP status <see cref="AnswerBankRequest"/> answers a well-formed request with (200: the bank).</summary>
    public HttpStatusCode BankHttpStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Every <c>api/char/bank</c> request served, oldest first: null when it met the contract, else why not.</summary>
    public ConcurrentQueue<string?> BankRequests { get; } = new();

    /// <summary>
    /// The game's <c>api/char/bank</c> endpoint as Game4000r9.swf's Game.requestAPI("bank", ...) uses it: a POST to
    /// https://game.aq.com/game/api/char/bank?v=&lt;Math.random()&gt;, request headers <c>ccid</c> (the character's
    /// objData.CharID) and <c>token</c> (the account's current web login token), a browser User-Agent, and the
    /// form body <c>layout={"cat":"all"}</c> and nothing else. Anything else is refused (HTTP 400/401/403), so a
    /// wrong request fails the test. The reply is the bank of the character that token and id belong to, as a JSON array.
    /// </summary>
    public HttpResponseMessage AnswerBankRequest(HttpRequestMessage request)
    {
        (string? refusal, HttpStatusCode code, Session? session) = CheckBankRequest(request);
        BankRequests.Enqueue(refusal);
        if (refusal is not null)
            return new HttpResponseMessage(code) { Content = new StringContent("refused: " + refusal) };
        if (BankHttpStatus != HttpStatusCode.OK)
            return new HttpResponseMessage(BankHttpStatus) { Content = new StringContent("<html>error page</html>") };
        JsonArray items = new(session!.Bank.Select(x => (JsonNode)x.DeepClone()).ToArray());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(items.ToJsonString(), Encoding.UTF8, "text/html") };
    }

    private (string? Refusal, HttpStatusCode Code, Session? Session) CheckBankRequest(HttpRequestMessage r)
    {
        Uri uri = r.RequestUri!;
        if (r.Method != HttpMethod.Post)
            return ($"method {r.Method}", HttpStatusCode.MethodNotAllowed, null);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "game.aq.com" || uri.AbsolutePath != "/game/api/char/bank")
            return ($"url {uri.Scheme}://{uri.Host}{uri.AbsolutePath}", HttpStatusCode.NotFound, null);
        if (!Regex.IsMatch(uri.Query, @"^\?v=0\.\d+$"))
            return ($"query {uri.Query}", HttpStatusCode.BadRequest, null);
        string? agent = r.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(agent) || !agent.StartsWith("Mozilla/", StringComparison.Ordinal))
            return ("no browser User-Agent", HttpStatusCode.Forbidden, null);
        if (!r.Headers.TryGetValues("ccid", out IEnumerable<string>? ccids) || ccids.Count() != 1 || !int.TryParse(ccids.Single(), out int ccid))
            return ("ccid header", HttpStatusCode.BadRequest, null);
        if (!r.Headers.TryGetValues("token", out IEnumerable<string>? tokens) || tokens.Count() != 1)
            return ("token header", HttpStatusCode.BadRequest, null);
        if (r.Content?.Headers.ContentType?.MediaType != "application/x-www-form-urlencoded")
            return ($"content type {r.Content?.Headers.ContentType?.MediaType}", HttpStatusCode.BadRequest, null);
        string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string[] fields = body.Split('&');
        if (fields.Length != 1 || !fields[0].StartsWith("layout=", StringComparison.Ordinal)
            || Uri.UnescapeDataString(fields[0]["layout=".Length..].Replace('+', ' ')) != """{"cat":"all"}""")
            return ("form body is not exactly layout={\"cat\":\"all\"}", HttpStatusCode.BadRequest, null);
        string token = tokens.Single();
        string account;
        lock (_tokenGate)
        {
            if (!_tokens.TryGetValue(token, out (string Account, bool Current, long IssuedAt) t) || !t.Current)
                return ("token is not the account's current one", HttpStatusCode.Unauthorized, null);
            account = t.Account;
        }
        Session? session = _sessions.Where(s => !s.Closed && s.LoggedIn && account.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Number).FirstOrDefault();
        if (session is null)
            return ("no character of that account is logged in", HttpStatusCode.Unauthorized, null);
        if (session.CharId != ccid)
            return ($"ccid {ccid} is not the character's CharID", HttpStatusCode.Forbidden, null);
        return (null, HttpStatusCode.OK, session);
    }

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
            foreach (int id in server.ExtraInventory)
                Inventory.Add(WithQty(Copy(id), _charItem++, 1, equip: false));
            // Gear enhanced with Lucky (pattern 9), as most farming gear is.
            foreach (JsonObject gear in Inventory.Where(x => (string?)x["sES"] is "ar" or "Weapon" or "he" or "ba"))
                gear["EnhPatternID"] = 9;
        }

        public int Number { get; }

        public int Uid { get; } = 40000 + Random.Shared.Next(1000);

        public string? Name { get; private set; }

        /// <summary>The character id initUserData sends (objData.CharID); not the web login's userid.</summary>
        public int CharId { get; } = 777;

        /// <summary>The socket login was accepted.</summary>
        public bool LoggedIn { get; private set; }

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
                    string version = Regex.Match(m, "<ver v='([^']*)'").Groups[1].Value;
                    _server.ApiVersions.Enqueue(version);
                    Raw(_server.RequiredApiVersion is string api && api != version
                        ? "<msg t='sys'><body action='apiKO' r='0'></body></msg>"
                        : "<msg t='sys'><body action='apiOK' r='0'></body></msg>");
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

                case "loadShop" when a.Length >= 1:
                    if (int.Parse(a[0], CultureInfo.InvariantCulture) != ShopId)
                        return;
                    Json(new JsonObject
                    {
                        ["cmd"] = "loadShop",
                        ["shopinfo"] = new JsonObject
                        {
                            ["ShopID"] = ShopId, ["sName"] = "Pixie Shop", ["bLimited"] = 0, ["sField"] = string.Empty, ["iIndex"] = -1,
                            ["items"] = new JsonArray(ShopStock.Select(x => (JsonNode)ShopItemJson(x)).ToArray()),
                        },
                    });
                    return;

                case "buyItem" when a.Length >= 3:
                    Buy(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture), a.Length >= 4 ? int.Parse(a[3], CultureInfo.InvariantCulture) : 1);
                    return;

                case "sellItem" when a.Length >= 3:
                    Sell(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture));
                    return;

                case "equipItem" when a.Length >= 1:
                    Equip(int.Parse(a[0], CultureInfo.InvariantCulture));
                    return;

                case "equipLoadout":
                case "wearLoadout":
                case "addLoadout":
                case "removeLoadout":
                    OnLoadout(cmd, a);
                    return;

                case "loadBank":
                    if (!_server.AnswerSocketLoadBank)
                        return;
                    Json(new JsonObject { ["cmd"] = "loadBank", ["bitSuccess"] = 1, ["items"] = new JsonArray(Bank.Select(x => (JsonNode)x.DeepClone()).ToArray()) });
                    return;

                default:
                    return;
            }
        }

        // equipItem: the slot's other item comes off; a class also changes the class (updateClass).
        private void Equip(int itemId)
        {
            JsonObject? item = Inventory.FirstOrDefault(x => (int)x["ItemID"]! == itemId);
            if (item is null)
                return;
            string slot = (string)item["sES"]!;
            foreach (JsonObject other in Inventory.Where(x => (string?)x["sES"] == slot))
                other["bEquip"] = 0;
            item["bEquip"] = 1;
            Json(new JsonObject
            {
                ["cmd"] = "equipItem", ["uid"] = Uid, ["ItemID"] = itemId, ["strES"] = slot,
                ["sFile"] = $"items/{itemId}.swf", ["sLink"] = $"Item{itemId}", ["sType"] = (string)item["sType"]!,
            });
            if ((string?)item["sType"] == "Class")
            {
                ClassName = (string)item["sName"]!;
                Json(new JsonObject { ["cmd"] = "updateClass", ["uid"] = Uid, ["sClassName"] = ClassName, ["iCP"] = (int)item["iQty"]!, ["sClassCat"] = "M1" });
            }
        }

        /// <summary>The class the character wears (objData.strClassName).</summary>
        public string ClassName { get; private set; } = "Healer";

        // The outfit commands as the game's OutfitSets interface sends them (outfitsets.as, outfitedit.as):
        //   equipLoadout / wearLoadout: "cmd", set name, keep colours 1|0;
        //   addLoadout: set name, the set as JSON without its name, the original name ("" for a new set);
        //   removeLoadout: set name.
        private void OnLoadout(string cmd, string[] a)
        {
            string? violation = cmd switch
            {
                "equipLoadout" or "wearLoadout" when a.Length != 3 => $"{a.Length} fields, the game sends 3",
                "equipLoadout" or "wearLoadout" when a[0] != "cmd" => $"first field {a[0]}, the game sends cmd",
                "equipLoadout" or "wearLoadout" when a[2] is not ("1" or "0") => $"colour flag {a[2]}, the game sends 1 or 0",
                "equipLoadout" or "wearLoadout" when a[1].Length == 0 => "empty set name",
                "addLoadout" when a.Length != 3 => $"{a.Length} fields, the game sends 3",
                "addLoadout" when !IsSetJson(a[1]) => "the set is not a JSON object of slots and colors",
                "removeLoadout" when a.Length != 1 => $"{a.Length} fields, the game sends 1",
                _ => null,
            };
            _server.LoadoutRequests.Enqueue((cmd, a, violation));
            if (violation is not null)
                return;
            JsonObject sets = _server.Loadouts;
            switch (cmd)
            {
                case "equipLoadout":
                    if (sets[a[1]] is not JsonObject set)
                        return;
                    foreach ((string slot, JsonNode? id) in set.ToList())
                    {
                        if (slot != "colors" && JsonUtil.AsIntOrNull(id) is int itemId && Inventory.Any(x => (int)x["ItemID"]! == itemId && (int)x["bEquip"]! == 0))
                            Equip(itemId);
                    }
                    return;

                case "wearLoadout":
                    if (sets[a[1]] is not JsonObject look)
                        return;
                    JsonObject costume = new();
                    foreach (string spot in new[] { "Weapon", "he", "ba", "co" })
                    {
                        if (JsonUtil.AsIntOrNull(look[spot]) is int wid && Inventory.Any(x => (int)x["ItemID"]! == wid))
                            costume[spot] = new JsonObject { ["ItemID"] = wid, ["sFile"] = $"items/{wid}.swf", ["sLink"] = $"Item{wid}", ["sType"] = (string)Items[wid]["sType"]! };
                    }
                    Json(new JsonObject { ["cmd"] = "wearLoadout", ["uid"] = Uid, ["success"] = 1, ["costume"] = costume });
                    return;

                case "addLoadout":
                    bool renaming = a[2].Length > 0 && sets.ContainsKey(a[2]);
                    if (!renaming && !sets.ContainsKey(a[0]) && sets.Count >= _server.LoadoutSlots)
                    {
                        Json(new JsonObject { ["cmd"] = "addLoadout", ["success"] = false, ["msg"] = "No free outfit slots." });
                        return;
                    }
                    if (renaming && a[2] != a[0])
                        sets.Remove(a[2]);
                    sets[a[0]] = JsonNode.Parse(a[1]);
                    Json(new JsonObject { ["cmd"] = "addLoadout", ["success"] = true });
                    return;

                case "removeLoadout":
                    bool removed = sets.Remove(a[0]);
                    Json(new JsonObject { ["cmd"] = "removeLoadout", ["success"] = removed, ["setName"] = a[0], ["msg"] = removed ? string.Empty : "No such outfit." });
                    return;
            }
        }

        private static bool IsSetJson(string text)
        {
            try
            {
                return JsonNode.Parse(text) is JsonObject o && o.All(p => p.Key == "colors"
                    ? p.Value is JsonObject
                    : p.Key is "he" or "ba" or "ar" or "co" or "Weapon" or "pe" or "am" or "mi" && JsonUtil.AsIntOrNull(p.Value) is not null);
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>Items bought (ItemID, quantity), in order.</summary>
        public ConcurrentQueue<(int ItemId, int Quantity)> Purchases { get; } = new();

        /// <summary>Items sold (ItemID, quantity), in order.</summary>
        public ConcurrentQueue<(int ItemId, int Quantity)> Sales { get; } = new();

        // buyItem: gold, membership and merge materials are checked; the item joins the backpack.
        private void Buy(int itemId, int shopItemId, int quantity)
        {
            (int ItemId, int ShopItemId, int Cost, bool Member, int MaterialId, int MaterialQty) s = ShopStock.FirstOrDefault(x => x.ItemId == itemId && x.ShopItemId == shopItemId);
            long cost = (long)s.Cost * Math.Max(1, quantity);
            JsonObject? material = s.MaterialId > 0 ? Inventory.FirstOrDefault(x => (int)x["ItemID"]! == s.MaterialId) : null;
            bool ok = s.ItemId != 0 && quantity > 0 && !s.Member && cost <= Gold
                && (s.MaterialId == 0 || (material is not null && (int)material["iQty"]! >= s.MaterialQty * quantity));
            if (!ok)
            {
                Json(new JsonObject { ["cmd"] = "buyItem", ["bitSuccess"] = 0, ["ItemID"] = itemId, ["strMessage"] = "Purchase refused." });
                return;
            }
            Gold -= (int)cost;
            JsonObject? held = Inventory.FirstOrDefault(x => (int)x["ItemID"]! == itemId);
            if (held is null)
            {
                held = WithQty(Copy(itemId), _charItem++, 0, equip: false);
                Inventory.Add(held);
            }
            held["iQty"] = (int)held["iQty"]! + quantity;
            Purchases.Enqueue((itemId, quantity));
            Json(new JsonObject
            {
                ["cmd"] = "buyItem", ["bitSuccess"] = 1, ["ItemID"] = itemId, ["CharItemID"] = (int)held["CharItemID"]!,
                ["iQty"] = quantity, ["iQtyNow"] = (int)held["iQty"]!, ["bBank"] = false,
            });
        }

        // sellItem: the backpack stack shrinks; the refund is a quarter of the shop price (Old Boots: 5 gold).
        private void Sell(int itemId, int quantity, int charItemId)
        {
            JsonObject? held = Inventory.FirstOrDefault(x => (int)x["CharItemID"]! == charItemId && (int)x["ItemID"]! == itemId);
            if (held is null || quantity < 1 || quantity > (int)held["iQty"]!)
                return;
            int now = (int)held["iQty"]! - quantity;
            held["iQty"] = now;
            if (now == 0)
                Inventory.Remove(held);
            Gold += 5 * quantity;
            Sales.Enqueue((itemId, quantity));
            Json(new JsonObject { ["cmd"] = "sellItem", ["CharItemID"] = charItemId, ["iQty"] = quantity, ["iQtyNow"] = now, ["intAmount"] = 5 * quantity, ["bCoins"] = 0 });
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
            string nickName = parts.Length > 1 ? parts[1] : "nobody";
            Name = nickName;
            ClientVersion = parts.Length > 2 ? parts[2] : string.Empty;
            if (_server.RequiredClientVersion is string required && Older(ClientVersion, required))
            {
                _server.LoginOutcomes.Enqueue((pword.Groups[1].Value, "client version"));
                Raw($"%xt%loginResponse%-1%0%-1%%Your game client is an older version. Server requires AQW game version {required} or higher. Please close the AQW game window and try again.%");
                return;
            }
            (string? refusal, string? account) = _server.RefusedClientVersions.Contains(ClientVersion)
                ? (CharacterDataRefusal, null)
                : _server.CheckLogin(nickName, pword.Groups[1].Value);
            _server.LoginOutcomes.Enqueue((pword.Groups[1].Value, refusal));
            if (refusal is not null)
            {
                Raw($"%xt%loginResponse%-1%0%-1%%{refusal}%");
                return;
            }
            // The server spells the name as the account has it (initUserData strUsername), whatever the nick's case.
            Name = account!;
            LoggedIn = true;
            Str("loginResponse", "true", Uid, Name.ToLowerInvariant(), _server.LoginMessage, string.Empty);
            // The live server pushes the account's preferences right after the login (seen 2026-11:
            // {"costumes":{},"loadouts":{},"prefs":{}} for an account without outfits).
            Json(new JsonObject
            {
                ["cmd"] = "loadPrefs", ["success"] = true,
                ["result"] = new JsonObject { ["costumes"] = new JsonObject(), ["loadouts"] = _server.Loadouts.DeepClone(), ["prefs"] = new JsonObject() },
            });
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
                ["strClassName"] = ClassName,
                ["iLoadoutSlots"] = _server.LoadoutSlots,
                ["iCP"] = 3000,
                ["CharID"] = CharId,
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
                    ["oRewards"] = new JsonObject { ["itemsS"] = new JsonObject { ["12"] = Copy(12) } },
                    ["reward"] = new JsonArray(new JsonObject { ["ItemID"] = 12, ["iQty"] = 1, ["iRate"] = 100, ["iType"] = 0 }),
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
