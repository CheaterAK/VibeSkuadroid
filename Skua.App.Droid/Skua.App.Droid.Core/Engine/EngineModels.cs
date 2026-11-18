namespace Skua.App.Droid.Core.Engine;

/// <summary>Coarse connection phase, as the UI and the notification show it.</summary>
public enum ConnectionPhase
{
    /// <summary>Not logged in.</summary>
    Offline,

    /// <summary>Web login (HTTP) in progress.</summary>
    LoggingIn,

    /// <summary>Socket connect, handshake and character load in progress.</summary>
    Connecting,

    /// <summary>Character loaded; commands can be sent.</summary>
    Online,

    /// <summary>The connection dropped and the engine is waiting to reconnect.</summary>
    Reconnecting,

    /// <summary>The last login or connection failed; <see cref="EngineSnapshot.StatusText"/> says why.</summary>
    Failed,
}

/// <summary>Lifecycle of the script runner.</summary>
public enum ScriptRunState
{
    Idle,
    Compiling,
    Running,
    Stopping,
    Faulted,
}

/// <summary>The script runner's state for the dashboard and the notification.</summary>
/// <param name="State">What the runner is doing.</param>
/// <param name="ScriptPath">The loaded script (full path), if any.</param>
/// <param name="StartedAt">When the current run started.</param>
/// <param name="Message">The last error or status line, if any.</param>
public sealed record ScriptStatus(ScriptRunState State, string? ScriptPath, DateTimeOffset? StartedAt, string? Message)
{
    public static ScriptStatus Idle { get; } = new(ScriptRunState.Idle, null, null, null);

    public string? ScriptName => ScriptPath is null ? null : Path.GetFileNameWithoutExtension(ScriptPath);

    public bool IsBusy => State is ScriptRunState.Compiling or ScriptRunState.Running or ScriptRunState.Stopping;
}

/// <summary>Whether this build can run scripts.</summary>
/// <param name="Available">False when scripts cannot run (no script host, or its compile references are incomplete).</param>
/// <param name="Reason">Shown to the user when <paramref name="Available"/> is false.</param>
public sealed record ScriptSupport(bool Available, string? Reason);

/// <summary>Process memory as sampled by the engine.</summary>
/// <param name="RssBytes">Resident set size of the whole process (what Android's low-memory killer looks at).</param>
/// <param name="PeakRssBytes">Peak RSS (VmHWM) since the process started.</param>
/// <param name="ManagedBytes">GC heap in use.</param>
/// <param name="ScriptAssembliesLoaded">Script assemblies loaded into this process so far. They are never freed on Mono/Android.</param>
/// <param name="SampledAt">When the sample was taken.</param>
public sealed record MemoryStatus(long RssBytes, long PeakRssBytes, long ManagedBytes, int ScriptAssembliesLoaded, DateTimeOffset SampledAt)
{
    public static MemoryStatus Unknown { get; } = new(-1, -1, -1, 0, DateTimeOffset.MinValue);

    public double RssMb => RssBytes < 0 ? double.NaN : RssBytes / 1048576.0;

    public double ManagedMb => ManagedBytes < 0 ? double.NaN : ManagedBytes / 1048576.0;
}

/// <summary>The logged-in character as the dashboard shows it.</summary>
public sealed record CharacterState(
    string Name,
    int Level,
    string ClassName,
    int Hp,
    int HpMax,
    int Mp,
    int MpMax,
    long Gold,
    string Map,
    string Cell,
    string Pad,
    int PlayersInRoom,
    bool Alive,
    bool InCombat,
    bool Afk);

/// <summary>An immutable picture of the engine for the UI and the notification.</summary>
public sealed record EngineSnapshot(
    ConnectionPhase Connection,
    string? StatusText,
    string? AccountName,
    string? ServerName,
    CharacterState? Character,
    ScriptStatus Script,
    MemoryStatus Memory,
    long WorldVersion,
    DateTimeOffset At)
{
    public static EngineSnapshot Initial { get; } = new(ConnectionPhase.Offline, null, null, null, null, ScriptStatus.Idle, MemoryStatus.Unknown, 0, DateTimeOffset.MinValue);

    /// <summary>Kills, deaths, quests and drops of the current or last script run (null without a Skua host).</summary>
    public ScriptCounters? ScriptCounters { get; init; }

    /// <summary>Long work the engine is doing besides the game (for example a script repository sync), or null.</summary>
    public string? BackgroundWork { get; init; }

    public bool IsOnline => Connection == ConnectionPhase.Online;

    /// <summary>
    /// The process must be kept alive in the background (the foreground service runs): a session is active, a script
    /// is busy, or background work runs.
    /// </summary>
    public bool NeedsForegroundService =>
        Connection is not (ConnectionPhase.Offline or ConnectionPhase.Failed) || Script.IsBusy || BackgroundWork is not null;

    /// <summary>Login or connect in progress, or waiting to reconnect.</summary>
    public bool IsBusy => Connection is ConnectionPhase.LoggingIn or ConnectionPhase.Connecting or ConnectionPhase.Reconnecting;
}

/// <summary>A login attempt.</summary>
/// <param name="Username">Account name.</param>
/// <param name="Password">Account password. Held in memory by the engine for reconnects; never logged.</param>
/// <param name="Server">Preferred server name; null or empty picks the least full usable server.</param>
/// <param name="ClientVersion">Forces the client version (blank discovers it).</param>
public sealed record LoginRequest(string Username, string Password, string? Server, string? ClientVersion = null)
{
    public override string ToString() => $"LoginRequest({Username}, <password redacted>, {Server ?? "auto"})";
}

/// <summary>A server for the picker.</summary>
public sealed record ServerChoice(string Name, int Players, int MaxPlayers, bool Online, bool MembersOnly, int MinLevel, string? Language)
{
    public string Display => $"{Name}  ({Players}/{MaxPlayers}){(MembersOnly ? "  members" : string.Empty)}{(Online ? string.Empty : "  offline")}";

    public override string ToString() => Display;
}

public enum EngineLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>A log line.</summary>
public sealed record LogEntry(DateTimeOffset At, EngineLogLevel Level, string Source, string Message)
{
    public string Display => $"{At.ToLocalTime():HH:mm:ss} {LevelTag} [{Source}] {Message}";

    private string LevelTag => Level switch
    {
        EngineLogLevel.Debug => "D",
        EngineLogLevel.Info => "I",
        EngineLogLevel.Warning => "W",
        _ => "E",
    };
}

public enum PacketDirection
{
    In,
    Out,
}

/// <summary>A captured packet.</summary>
public sealed record PacketEntry(DateTimeOffset At, PacketDirection Direction, string Text)
{
    public string Display => $"{At.ToLocalTime():HH:mm:ss.fff} {(Direction == PacketDirection.In ? "<-" : "->")} {Text}";
}

/// <summary>An inventory, temp inventory or bank row.</summary>
public sealed record InventoryRow(int ItemId, string Name, int Quantity, int MaxStack, string Type, string Slot, bool Equipped, bool Temp, bool Member)
{
    public string QuantityText => MaxStack > 1 ? $"{Quantity}/{MaxStack}" : $"x{Quantity}";
}

/// <summary>A quest row (loaded and/or accepted).</summary>
public sealed record QuestRow(int QuestId, string Name, bool Accepted, bool Completable, int Level, bool Member)
{
    public string StatusText => Completable ? "ready to turn in" : Accepted ? "in progress" : "loaded";
}

/// <summary>A drop waiting to be picked up.</summary>
public sealed record DropRow(int ItemId, string Name, int Quantity, int TimesOffered);

/// <summary>A monster in the current map.</summary>
public sealed record MonsterRow(int MonMapId, string Name, int Level, string Cell, int Hp, int HpMax, bool Alive);

/// <summary>The read-only lists the world screens show.</summary>
public sealed record WorldLists(
    IReadOnlyList<InventoryRow> Inventory,
    IReadOnlyList<InventoryRow> TempInventory,
    IReadOnlyList<InventoryRow> Bank,
    bool BankLoaded,
    IReadOnlyList<QuestRow> Quests,
    IReadOnlyList<DropRow> Drops,
    IReadOnlyList<MonsterRow> Monsters,
    long WorldVersion)
{
    public static WorldLists Empty { get; } = new([], [], [], false, [], [], [], 0);
}

/// <summary>A failure with a message fit for the user (no secrets, no stack trace).</summary>
public sealed class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
