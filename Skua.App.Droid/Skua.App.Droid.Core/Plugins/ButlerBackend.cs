namespace Skua.App.Droid.Core.Plugins;

/// <summary>Where the butler goes when it gives up on reaching the player or is stopped with "park" (ButlerV5's ParkSpot).</summary>
public enum ButlerParkSpot
{
    /// <summary>Its own house (whitemap when it has none or the join fails).</summary>
    House,

    /// <summary>The empty whitemap.</summary>
    Whitemap,

    /// <summary>Do not move.</summary>
    Stay,
}

/// <summary>Which CoreBots class the butler equips when it starts following (ButlerV5's ButlerClassType).</summary>
public enum ButlerClassChoice
{
    /// <summary>Keep the current gear.</summary>
    None,
    Farm,
    Solo,
    Dodge,
    Boss,
}

/// <summary>
/// The follow options of ButlerV5 that mean something for one headless account. Defaults are the desktop plugin's.
/// </summary>
public sealed record ButlerOptions
{
    /// <summary>Only fight while the followed player is in combat (desktop: "Only attack when master attacks").</summary>
    public bool PassiveAttack { get; init; }

    /// <summary>Milliseconds between goto attempts while chasing (the loop tick, 250 ms, is the floor).</summary>
    public int GotoDelayMs { get; init; } = 500;

    /// <summary>Keep sending goto even when already in the player's room (fixes butlers stuck in a wrong cell).</summary>
    public bool AlwaysGoto { get; init; }

    /// <summary>Mirror the player's walking inside the same cell.</summary>
    public bool CopyWalk { get; init; }

    /// <summary>Stay in the player's room but wait at Enter and never fight.</summary>
    public bool LeechMode { get; init; }

    /// <summary>Where to park when the player cannot be reached (or on "Stop and park").</summary>
    public ButlerParkSpot Park { get; init; } = ButlerParkSpot.House;

    /// <summary>Failed gotos before a reach attempt counts as failed (desktop: "Failed gotos before rescue").</summary>
    public int RescueThreshold { get; init; } = 3;

    /// <summary>React to "locked zone" / "room full" / "ignoring goto" warnings in the packet stream at once.</summary>
    public bool InstantWarnings { get; init; } = true;

    /// <summary>CoreBots class to equip when the follow starts (read from CoreBots' options for this account).</summary>
    public ButlerClassChoice ClassType { get; init; } = ButlerClassChoice.None;

    /// <summary>Record diagnostics in the log view (off: only the main events).</summary>
    public bool DebugLog { get; init; } = true;

    /// <summary>Values a hand-edited file could carry, clamped as the desktop plugin clamps them.</summary>
    public ButlerOptions Normalized() => this with
    {
        GotoDelayMs = GotoDelayMs > 0 ? Math.Clamp(GotoDelayMs, 0, 60000) : 500,
        RescueThreshold = RescueThreshold > 0 ? Math.Clamp(RescueThreshold, 1, 10) : 3,
    };
}

/// <summary>What the butler does.</summary>
public enum ButlerFollowState
{
    /// <summary>Not following anyone.</summary>
    Idle,

    /// <summary>Following, but this account is logged out or dead.</summary>
    Waiting,

    /// <summary>Going to the player (goto).</summary>
    Traveling,

    /// <summary>In the player's room.</summary>
    WithPlayer,

    /// <summary>Gave up for now (player unreachable); retrying slowly.</summary>
    Parked,

    /// <summary>A script runs on this account; following resumes when it stops.</summary>
    Suspended,
}

/// <summary>The butler's state for the page.</summary>
/// <param name="Player">Who it follows (null when idle).</param>
/// <param name="State">What it does.</param>
/// <param name="Detail">One line for the user.</param>
/// <param name="FailedGotos">Gotos sent since it last saw the player.</param>
public sealed record ButlerStatus(string? Player, ButlerFollowState State, string Detail, int FailedGotos)
{
    public static ButlerStatus Idle { get; } = new(null, ButlerFollowState.Idle, "Not following anyone.", 0);

    public bool IsFollowing => Player is not null;
}

public enum ButlerLogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One line of the butler's log (a repeat of the previous line raises its <paramref name="Count"/>).</summary>
public sealed record ButlerLogLine(long Seq, DateTimeOffset At, ButlerLogLevel Level, string Component, string Text, int Count)
{
    public string Display => $"{At.ToLocalTime():HH:mm:ss} [{Component}] {Text}{(Count > 1 ? $" (x{Count})" : string.Empty)}";
}

/// <summary>
/// ButlerV5 for one account: follow another player by name (goto, then their cell, fighting beside them), send the
/// account to a player once, and the plugin's log. The multi-account parts (summon, roster, sync files, send-all) need
/// several local accounts and are not part of this interface.
/// </summary>
public interface IButlerBackend
{
    /// <summary>The live options (read on every loop tick).</summary>
    ButlerOptions Options { get; set; }

    ButlerStatus Status { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler<ButlerStatus>? StatusChanged;

    /// <summary>A line was added or the log was cleared (any thread).</summary>
    event EventHandler? LogChanged;

    IReadOnlyList<ButlerLogLine> GetLog();

    void ClearLog();

    /// <summary>Starts following <paramref name="player"/> (stops any current follow first).</summary>
    /// <exception cref="Engine.EngineException">Not logged in, a script is running, or the name is this account's.</exception>
    void StartFollow(string player);

    /// <summary>Stops following; with <paramref name="park"/> the account then goes to its park spot.</summary>
    void StopFollow(bool park = false);

    /// <summary>Sends this account to <paramref name="player"/> once (the game's /goto).</summary>
    /// <exception cref="Engine.EngineException">Not logged in or a script is running.</exception>
    void GotoPlayer(string player);

    /// <summary>Jumps to <paramref name="player"/>'s cell when they are in this room.</summary>
    /// <exception cref="Engine.EngineException">Not logged in, or the player is not in this room.</exception>
    void JumpToPlayer(string player);

    /// <summary>The other players in this room.</summary>
    IReadOnlyList<string> PlayersInRoom();
}
