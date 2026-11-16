using Skua.Core.Headless;
using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Globalization;

namespace Skua.Headless.Cli;

/// <summary>
/// Smoke-test console for the headless AQW client: log in, pick a server, join a map, print the model, optionally
/// run a short script. Credentials come only from AQW_USER/AQW_PASS or a dotenv file given with
/// --credentials-file; they are never accepted on the command line.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage: skua-headless [options]

          --credentials-file PATH   dotenv file with AQW_USER=... and AQW_PASS=...
                                    (otherwise the AQW_USER / AQW_PASS environment variables are used)
          --server NAME             preferred server (default: least populated usable one)
          --map NAME                map to join after login (default: stay where the server puts us)
          --cell CELL --pad PAD     cell/pad for --map (default Enter/Spawn)
          --script PATH             run commands from a file after joining (see below)
          --client-version X.YY     skip discovery and use this client version
          --api-base URL            web API root (default https://game.aq.com/game/)
          --show-packets            print every packet in and out (the login token is redacted)
          --allow-raw               allow 'raw' lines in the script
          --list-servers            print the server list after the web login and exit

        Script lines (# starts a comment):
          join MAP [CELL] [PAD]     jump CELL [PAD]          wait MS
          attack MONMAPID|NAME|* [SKILL] [TIMES]            rest
          loadquests ID...          accept ID                complete ID [REWARD]
          bank                      getdrop ITEMID           drops
          print                     probe                    raw PACKET (needs --allow-raw)
        """;

    private static readonly string[] s_forbidden = { "--user", "--username", "--pass", "--password", "-u", "-p" };

    private static async Task<int> Main(string[] argv)
    {
        Dictionary<string, string?> args;
        try
        {
            args = ParseArgs(argv);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (args.ContainsKey("--help") || args.ContainsKey("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        AqwCredentials? credentials;
        try
        {
            credentials = args.TryGetValue("--credentials-file", out string? file) && file is not null
                ? AqwCredentials.FromEnvFile(file)
                : AqwCredentials.FromEnvironment();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read credentials: {e.Message}");
            return 2;
        }
        if (credentials is null)
        {
            Console.Error.WriteLine("No credentials: set AQW_USER and AQW_PASS, or pass --credentials-file PATH.");
            return 2;
        }

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AqwSessionOptions options = new();
        if (args.TryGetValue("--client-version", out string? version) && version is not null)
            options.ClientVersion = version;
        if (args.TryGetValue("--api-base", out string? apiBase) && apiBase is not null)
            options.WebApi.BaseUri = new Uri(apiBase.EndsWith('/') ? apiBase : apiBase + "/");

        await using AqwSession session = new(options);
        session.Log += (_, e) => Console.Error.WriteLine($"{Now()} [{e.Level}] {e.Message}");
        session.Disconnected += (_, e) => Console.Error.WriteLine($"{Now()} disconnected: {e.Reason} ({e.Message}); reconnect advised: {e.ShouldReconnect}");
        if (args.ContainsKey("--show-packets"))
        {
            session.PacketReceived += (_, p) => Console.WriteLine($"{Now()} <- {Shorten(p.Text)}{(p.WasCompressed ? "  [Z]" : string.Empty)}");
            session.PacketSent += (_, e) => Console.WriteLine($"{Now()} -> {Shorten(e.Text)}");
        }

        try
        {
            LoginResult account = await session.LoginAsync(credentials, cts.Token);
            Console.WriteLine($"Web login ok: {account.Username}, level {account.Level}, member {account.IsMember}, {account.Servers.Count} servers.");
            if (args.ContainsKey("--list-servers"))
            {
                foreach (GameServer s in account.Servers.OrderBy(s => s.Name))
                    Console.WriteLine($"  {s}  {s.Host}:{s.Port}  {s.Refusal(account.IsMember, account.Level) ?? "usable"}");
                return 0;
            }

            GameServer server = session.PickServer(args.GetValueOrDefault("--server"));
            Console.WriteLine($"Connecting to {server} ...");
            await session.ConnectAsync(server, cts.Token);
            Console.WriteLine($"Connected with client version {session.ClientVersion!.Version} ({session.ClientVersion.Source}).");
        }
        catch (AqwLoginException e)
        {
            Console.Error.WriteLine($"Login failed ({e.Kind}): {e.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }

        int exit = 0;
        try
        {
            GameCommands commands = session.Commands;
            if (args.TryGetValue("--map", out string? map) && map is not null)
            {
                AreaInfo area = await commands.JoinMapAsync(map, args.GetValueOrDefault("--cell") ?? "Enter", args.GetValueOrDefault("--pad") ?? "Spawn", cancellationToken: cts.Token);
                Console.WriteLine($"Joined {area.AreaName}.");
                await Task.Delay(1000, cts.Token);
            }

            PrintState(session.World);

            if (args.TryGetValue("--script", out string? script) && script is not null)
            {
                await RunScriptAsync(session, script, args.ContainsKey("--allow-raw"), cts.Token);
                PrintState(session.World);
            }
        }
        catch (Exception e) when (e is JoinFailedException or TimeoutException or ConnectionClosedException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine($"Failed: {e.Message}");
            exit = 5;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            exit = 130;
        }

        PrintCounters(session);
        await session.DisconnectAsync();
        return exit;
    }

    private static Dictionary<string, string?> ParseArgs(string[] argv)
    {
        HashSet<string> flags = new() { "--show-packets", "--allow-raw", "--list-servers", "--help", "-h" };
        HashSet<string> valued = new() { "--credentials-file", "--server", "--map", "--cell", "--pad", "--script", "--client-version", "--api-base" };
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (s_forbidden.Contains(a, StringComparer.OrdinalIgnoreCase) || a.StartsWith("--pass", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Credentials are never taken from the command line: use AQW_USER/AQW_PASS or --credentials-file.");
            if (flags.Contains(a))
                result[a] = null;
            else if (valued.Contains(a) && i + 1 < argv.Length)
                result[a] = argv[++i];
            else
                throw new ArgumentException($"Unknown or incomplete option: {a}");
        }
        return result;
    }

    private static async Task RunScriptAsync(AqwSession session, string path, bool allowRaw, CancellationToken cancellationToken)
    {
        GameCommands cmd = session.Commands;
        WorldModel w = session.World;
        int lineNo = 0;
        foreach (string raw in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine($"> {line}");
            switch (p[0].ToLowerInvariant())
            {
                case "join":
                    AreaInfo area = await cmd.JoinMapAsync(p[1], p.ElementAtOrDefault(2) ?? "Enter", p.ElementAtOrDefault(3) ?? "Spawn", cancellationToken: cancellationToken);
                    Console.WriteLine($"  joined {area.AreaName} ({area.MonsterCount} monster slots, {area.UserCount} players)");
                    break;

                case "jump":
                    await cmd.JumpAsync(p[1], p.ElementAtOrDefault(2) ?? "Spawn");
                    Console.WriteLine($"  now in {w.GetArea().Cell}");
                    break;

                case "attack":
                    await AttackAsync(cmd, w, p.ElementAtOrDefault(1) ?? "*", p.ElementAtOrDefault(2) ?? "aa", int.Parse(p.ElementAtOrDefault(3) ?? "1", CultureInfo.InvariantCulture), cancellationToken);
                    break;

                case "rest":
                    await cmd.RestAsync();
                    break;

                case "loadquests":
                    foreach (QuestInfo q in await cmd.LoadQuestsAsync(p.Skip(1).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(), cancellationToken: cancellationToken))
                        Console.WriteLine($"  quest {q.QuestId} {q.Name} (level {q.Level}{(q.Member ? ", member" : string.Empty)})");
                    break;

                case "accept":
                    Console.WriteLine($"  accepted: {await cmd.AcceptQuestAsync(int.Parse(p[1], CultureInfo.InvariantCulture), cancellationToken: cancellationToken)}");
                    break;

                case "complete":
                    QuestCompletionResult result = await cmd.TryCompleteQuestAsync(int.Parse(p[1], CultureInfo.InvariantCulture), p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : -1, cancellationToken: cancellationToken);
                    Console.WriteLine($"  completed: {result.Success}{(result.Message is null ? string.Empty : $" ({result.Message})")}");
                    break;

                case "bank":
                    IReadOnlyList<ItemInfo> bank = await cmd.LoadBankAsync(cancellationToken: cancellationToken);
                    Console.WriteLine($"  bank: {bank.Count} items");
                    foreach (ItemInfo item in bank.Take(20))
                        Console.WriteLine($"    {item.Name} x{item.Quantity}");
                    break;

                case "getdrop":
                    Console.WriteLine($"  picked up: {await cmd.GetDropAsync(int.Parse(p[1], CultureInfo.InvariantCulture), cancellationToken: cancellationToken)}");
                    break;

                case "drops":
                    foreach (DropInfo d in w.GetDrops())
                        Console.WriteLine($"  drop {d.ItemId} {d.Name} x{d.Quantity}");
                    break;

                case "wait":
                    await Task.Delay(int.Parse(p[1], CultureInfo.InvariantCulture), cancellationToken);
                    break;

                case "print":
                    PrintState(w);
                    break;

                case "probe":
                    Console.WriteLine($"  alive: {await session.Connection!.ProbeAsync(cancellationToken: cancellationToken)}");
                    break;

                case "raw":
                    if (!allowRaw)
                        throw new InvalidDataException($"line {lineNo}: 'raw' needs --allow-raw");
                    await cmd.SendRawAsync(line[4..].Trim());
                    break;

                default:
                    throw new InvalidDataException($"line {lineNo}: unknown command '{p[0]}'");
            }
        }
    }

    // Attacks a monster in the current cell (by MonMapID, name, or * for any) until it dies or TIMES hits are sent.
    // Paced like any other packet; this is a smoke test, not a combat loop (skill rules are the next layer).
    private static async Task AttackAsync(GameCommands cmd, WorldModel w, string which, string skill, int times, CancellationToken cancellationToken)
    {
        for (int i = 0; i < times; i++)
        {
            string cell = w.GetArea().Cell;
            MonsterInfo? target = w.GetMonsters()
                .Where(m => m.IsAlive && m.Cell.Equals(cell, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(m => which == "*" || m.MonMapId.ToString(CultureInfo.InvariantCulture) == which || m.Name.Equals(which, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.WriteLine($"  no live '{which}' in cell {cell}");
                return;
            }
            ActionResult r = await cmd.AttackMonsterAsync(target.MonMapId, skill, TimeSpan.FromSeconds(3), cancellationToken);
            MonsterInfo? after = w.GetMonster(target.MonMapId);
            Console.WriteLine($"  {skill} -> {target.Name} [{target.MonMapId}]: {(r.Reply is null ? "no combat reply" : r.Reply.Command)}, hp {after?.Hp}/{after?.HpMax}");
            if (after is { IsAlive: false })
            {
                Console.WriteLine($"  {target.Name} defeated");
                return;
            }
            await Task.Delay(2000, cancellationToken); // stay near the auto-attack rhythm
        }
    }

    private static void PrintState(WorldModel w)
    {
        PlayerInfo? me = w.GetPlayer();
        AreaInfo area = w.GetArea();
        Console.WriteLine("---- state ----");
        if (me is not null)
        {
            Console.WriteLine($"Player   {me.Username} (uid {me.UserId}) level {me.Level} {me.ClassName}, gold {me.Gold}, xp {me.Exp}/{me.ExpToLevel}");
            Console.WriteLine($"         HP {me.Hp}/{me.HpMax} MP {me.Mp}/{me.MpMax} state {me.State}{(me.Afk ? " AFK" : string.Empty)}");
        }
        Console.WriteLine($"Area     {area.AreaName} (room {area.RoomId}) cell {area.Cell}/{area.Pad}, {area.UserCount} players");
        foreach (MonsterInfo m in w.GetMonsters().Take(12))
            Console.WriteLine($"  mon    [{m.MonMapId}] {m.Name} lvl {m.Level} in {m.Cell}: {m.Hp}/{m.HpMax} state {m.State}");
        IReadOnlyList<ItemInfo> inv = w.GetInventory();
        Console.WriteLine($"Items    {inv.Count} in backpack, {w.GetTempInventory().Count} temp, {w.GetHouseInventory().Count} house, bank {(w.IsBankLoaded ? w.GetBank().Count.ToString(CultureInfo.InvariantCulture) : "not loaded")}");
        foreach (ItemInfo i in inv.Where(i => i.Equipped))
            Console.WriteLine($"  worn   {i.EquipSlot}: {i.Name}");
        foreach (ItemInfo i in w.GetTempInventory())
            Console.WriteLine($"  temp   {i.Name} x{i.Quantity}");
        Console.WriteLine($"Skills   {string.Join(", ", w.GetSkills().Where(s => !s.Passive).Select(s => $"{s.Ref}:{s.Name}"))}");
        Console.WriteLine($"Quests   loaded {w.GetQuests().Count(q => q.Loaded)}, accepted [{string.Join(", ", w.GetAcceptedQuestIds())}]");
        Console.WriteLine($"Factions {w.GetFactions().Count}; drops waiting {w.GetDrops().Count}");
        Console.WriteLine("---------------");
    }

    private static void PrintCounters(AqwSession session)
    {
        PacketCounters c = session.World.GetPacketCounters();
        Console.WriteLine($"Packets  {c.Total} received; handled kinds {c.Handled.Count}, unhandled kinds {c.Unhandled.Count}");
        foreach (KeyValuePair<string, long> u in c.Unhandled.OrderByDescending(x => x.Value))
            Console.WriteLine($"  unhandled {u.Key} x{u.Value}: {Shorten(c.UnhandledSamples.GetValueOrDefault(u.Key) ?? string.Empty)}");
        if (session.Connection?.GetStatistics() is ConnectionStatistics s)
            Console.WriteLine($"Socket   in {s.PacketsIn} ({s.CompressedIn} compressed, {s.UndecodableIn} undecodable), out {s.PacketsOut}, dropped {s.OutboundDropped}, slow-down warnings {s.SlowDownWarnings}, AFK clears {s.AfkClears}, probes {s.Probes}");
    }

    private static string Now()
    {
        return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static string Shorten(string text)
    {
        return text.Length > 220 ? text[..220] + "..." : text;
    }
}
