using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Commands;

/// <summary>Outcome of a quest turn-in (<c>ccqr</c>).</summary>
public sealed record QuestCompletionResult(int QuestId, bool Success, string? Message, JsonObject? Raw);

/// <summary>Outcome of a <c>gar</c> action request.</summary>
/// <param name="Reply">The combat packet (<c>ct</c>/<c>sars</c>/<c>sar</c>) that reported the action, or null if none arrived in time.</param>
public sealed record ActionResult(int ActionId, string SkillRef, string Target, bool Sent, InboundPacket? Reply);

/// <summary>A join the server refused (warning) or never answered.</summary>
public sealed class JoinFailedException : Exception
{
    public JoinFailedException(string map, string message)
        : base(message)
    {
        Map = map;
    }

    public string Map { get; }
}

/// <summary>
/// The few game actions a smoke test needs, as request/reply pairs over one <see cref="AqwConnection"/>.
/// Each method sends exactly what Skua.Core sends for the same action and waits for the server's reply.
/// </summary>
/// <remarks>
/// Deliberately thin: no client-side rules (skill readiness, target selection, auto attack, quest requirement
/// checks, inventory space). Those belong to the next layer, which can call <see cref="SendRawAsync"/> or the
/// connection directly.
/// </remarks>
public sealed class GameCommands
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly AqwConnection _connection;
    private readonly Func<CancellationToken, Task<BankLoadResult>>? _bankLoader;

    public GameCommands(AqwConnection connection)
        : this(connection, null)
    {
    }

    /// <param name="connection">The connection the commands go over.</param>
    /// <param name="bankLoader">Loads the bank (the session's <see cref="AqwSession.LoadBankAsync"/>); null asks over the socket.</param>
    public GameCommands(AqwConnection connection, Func<CancellationToken, Task<BankLoadResult>>? bankLoader)
    {
        _connection = connection;
        _bankLoader = bankLoader;
    }

    public AqwConnection Connection => _connection;

    private WorldModel World => _connection.World;

    /// <summary>
    /// <c>%xt%zm%cmd%room%tfer%user%map%cell%pad%</c>; waits for <c>moveToArea</c>. The connection then enters
    /// the cell (moveToCell) on its own.
    /// </summary>
    /// <exception cref="JoinFailedException">The server answered with a warning, or nothing came in time.</exception>
    public async Task<AreaInfo> JoinMapAsync(string map, string cell = "Enter", string pad = "Spawn", TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);
        string username = World.GetPlayer()?.Username ?? World.MyUsername ?? throw new InvalidOperationException("Not logged in.");
        string mapName = map.Split('-')[0];
        string packet = Packets.Transfer(World.RoomId, username, map, cell, pad);
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            packet,
            p => (p.Kind == PacketKind.Json && p.Command == "moveToArea")
                || (p.Kind == PacketKind.Xt && p.Command == "warning" && IsJoinRefusal(string.Join(' ', p.Args), mapName)),
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new JoinFailedException(map, $"No reply to the join of '{map}'.");
        if (reply.Kind == PacketKind.Xt)
            throw new JoinFailedException(map, string.Join(' ', reply.Args));
        return World.GetArea();
    }

    private static bool IsJoinRefusal(string text, string map)
    {
        return text.Contains(map, StringComparison.OrdinalIgnoreCase)
            || text.Contains("not available", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot join", StringComparison.OrdinalIgnoreCase)
            || text.Contains("must be", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is full", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>moveToCell</c>. The server only answers with a <c>uotls</c> for other players, so this returns once
    /// the packet is written; the model moves the character at once, as the Flash client does.
    /// </summary>
    public Task<bool> JumpAsync(string cell, string pad = "Spawn")
    {
        return _connection.SendAsync(Packets.MoveToCell(World.RoomId, cell, pad));
    }

    /// <summary>
    /// <c>%xt%zm%gar%1%&lt;actId&gt;%&lt;ref&gt;&gt;m:&lt;MonMapID&gt;%wvz%</c>, and waits (up to
    /// <paramref name="wait"/>) for the combat packet that reports it. Skill readiness is not checked.
    /// </summary>
    public Task<ActionResult> AttackMonsterAsync(int monMapId, string skillRef = "aa", TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        return UseSkillAsync(skillRef, $"m:{monMapId}", wait, cancellationToken);
    }

    /// <summary>Any <c>gar</c>: <paramref name="target"/> is <c>m:&lt;MonMapID&gt;</c> or <c>p:&lt;uid&gt;</c>.</summary>
    public async Task<ActionResult> UseSkillAsync(string skillRef, string target, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        int actionId = _connection.NextActionId();
        string packet = Packets.Gar(actionId, skillRef, new[] { target });
        string me = $"p:{World.MyUserId}";
        TimeSpan timeout = wait ?? TimeSpan.FromSeconds(3);
        if (timeout <= TimeSpan.Zero)
            return new ActionResult(actionId, skillRef, target, await _connection.SendAsync(packet).ConfigureAwait(false), null);
        InboundPacket? reply = await _connection.SendAndWaitAsync(packet, p => ReportsAction(p, actionId, me), timeout, cancellationToken).ConfigureAwait(false);
        return new ActionResult(actionId, skillRef, target, true, reply);
    }

    private static bool ReportsAction(InboundPacket p, int actionId, string me)
    {
        if (p.Kind != PacketKind.Json || p.Data is null)
            return false;
        if (p.Command is "sar" or "sars")
            return MatchesAction(p.Data, actionId, me);
        if (p.Command is not ("ct" or "cb"))
            return false;
        foreach (string list in new[] { "sarsa", "sara" })
        {
            if (p.Data[list] is JsonArray results && results.OfType<JsonObject>().Any(r => MatchesAction(r, actionId, me)))
                return true;
        }
        return false;
    }

    private static bool MatchesAction(JsonObject r, int actionId, string me)
    {
        if (JsonUtil.AsIntOrNull(r["actID"]) == actionId)
            return true;
        JsonObject? result = r["actionResult"] as JsonObject;
        return JsonUtil.AsIntOrNull(result?["actID"]) == actionId
            || (r["actID"] is null && JsonUtil.AsString(r["cInf"] ?? result?["cInf"]) == me);
    }

    /// <summary><c>getQuests</c>: loads quest definitions into <c>world.questTree</c>.</summary>
    public async Task<IReadOnlyList<QuestInfo>> LoadQuestsAsync(IReadOnlyCollection<int> questIds, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (questIds.Count == 0)
            return Array.Empty<QuestInfo>();
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            Packets.GetQuests(World.RoomId, questIds),
            p => p.Kind == PacketKind.Json && p.Command is "getQuests" or "getQuests2",
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new TimeoutException($"No reply to getQuests {string.Join(',', questIds)}.");
        return questIds.Select(World.GetQuest).Where(q => q is not null).Cast<QuestInfo>().ToList();
    }

    /// <summary><c>acceptQuest</c>; true when the server confirms it.</summary>
    public async Task<bool> AcceptQuestAsync(int questId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            Packets.AcceptQuest(World.RoomId, questId),
            p => p.Kind == PacketKind.Json && p.Command == "acceptQuest" && JsonUtil.AsInt(p.Data?["QuestID"]) == questId,
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new TimeoutException($"No reply to acceptQuest {questId}.");
        return reply.Data?["bSuccess"] is not JsonNode success || JsonUtil.AsBool(success);
    }

    /// <summary><c>tryQuestComplete</c> as Skua.Core sends it; waits for <c>ccqr</c>.</summary>
    public async Task<QuestCompletionResult> TryCompleteQuestAsync(int questId, int rewardItemId = -1, int turnIns = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            Packets.TryQuestComplete(World.RoomId, questId, rewardItemId, special: false, turnIns),
            p => p.Kind == PacketKind.Json && p.Command == "ccqr" && JsonUtil.AsInt(p.Data?["QuestID"]) == questId,
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new TimeoutException($"No reply to tryQuestComplete {questId}.");
        return new QuestCompletionResult(
            questId,
            JsonUtil.AsBool(reply.Data?["bSuccess"]),
            JsonUtil.AsString(reply.Data?["msg"]) ?? JsonUtil.AsString(reply.Data?["strMessage"]),
            (JsonObject?)reply.Data?.DeepClone());
    }

    /// <summary>
    /// Loads the bank and returns it. From <see cref="AqwSession.Commands"/>: the game's way
    /// (<see cref="AqwSession.LoadBankAsync"/>, HTTP <c>api/char/bank</c>, socket fallback). On a bare connection
    /// (no session, so no login token): the socket request <c>loadBank All</c>.
    /// </summary>
    /// <exception cref="TimeoutException">The bank did not load; the message says why.</exception>
    public async Task<IReadOnlyList<ItemInfo>> LoadBankAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await EnsureBankLoadedAsync(timeout, cancellationToken).ConfigureAwait(false);
        return World.GetBank();
    }

    /// <summary>
    /// <see cref="LoadBankAsync"/> without copying the bank for the caller: how many items it holds (a 3000-item
    /// bank copied with every item's stored object is 12 MB).
    /// </summary>
    /// <exception cref="TimeoutException">The bank did not load; the message says why.</exception>
    public async Task<int> EnsureBankLoadedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (_bankLoader is not null)
        {
            BankLoadResult result = await _bankLoader(cancellationToken).ConfigureAwait(false);
            if (!result.Loaded)
                throw new TimeoutException($"The bank did not load: {result.Failure}");
            return World.BankCount;
        }
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            Packets.LoadBank(World.RoomId),
            p => p.Kind == PacketKind.Json && p.Command == "loadBank",
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new TimeoutException("No reply to loadBank.");
        return World.BankCount;
    }

    /// <summary><c>getDrop</c>; true when the server hands the item over.</summary>
    public async Task<bool> GetDropAsync(int itemId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        InboundPacket? reply = await _connection.SendAndWaitAsync(
            Packets.GetDrop(World.RoomId, itemId),
            p => p.Kind == PacketKind.Json && p.Command == "getDrop" && JsonUtil.AsInt(p.Data?["ItemID"]) == itemId,
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            throw new TimeoutException($"No reply to getDrop {itemId}.");
        return JsonUtil.AsBool(reply.Data?["bSuccess"]);
    }

    /// <summary><c>restRequest</c> as Skua.Core sends it.</summary>
    public Task<bool> RestAsync()
    {
        return _connection.SendAsync(Packets.Rest());
    }

    /// <summary>Sends any packet (paced, AFK-cleared like every other). Nothing is checked.</summary>
    public Task<bool> SendRawAsync(string packet)
    {
        return _connection.SendAsync(packet);
    }
}
