extern alias desktop;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Engine;
using Skua.Core.Headless.Net;
using Skua.Core.Models.Packets;
using DesktopPacketCommands = desktop::Skua.App.Avalonia.ViewModels.Packets.PacketCommands;

namespace Skua.App.Droid.Core.Packets;

/// <summary>Which messages a rule looks at.</summary>
public enum InterceptDirection
{
    /// <summary>Client to server.</summary>
    Outbound,

    /// <summary>Server to client.</summary>
    Inbound,

    Both,
}

/// <summary>What a rule does to a message it matches.</summary>
public enum InterceptAction
{
    /// <summary>Replaces every <see cref="InterceptRule.Find"/> by <see cref="InterceptRule.Replace"/>.</summary>
    Replace,

    /// <summary>Drops the message.</summary>
    Block,
}

/// <summary>
/// One interceptor rule: messages of <see cref="Direction"/>, whose commands include <see cref="Command"/> (when
/// given) and whose text contains <see cref="Find"/> (when given, case-sensitive), are changed or dropped.
/// </summary>
public sealed partial record InterceptRule(
    string Id,
    string Name,
    bool Enabled,
    InterceptDirection Direction,
    string Command,
    string Find,
    InterceptAction Action,
    string Replace)
{
    public const int MaxFindLength = 500;

    public const int MaxReplaceLength = 2000;

    public bool Matches(bool outbound, string text, IReadOnlyList<string> commands) =>
        Enabled
        && (Direction == InterceptDirection.Both || (Direction == InterceptDirection.Outbound) == outbound)
        && (Command.Length == 0 || commands.Contains(Command, StringComparer.Ordinal))
        && (Find.Length == 0 || text.Contains(Find, StringComparison.Ordinal));

    /// <summary>Why the rule cannot be saved, or null.</summary>
    public string? Validate()
    {
        if (Command.Length > 0 && !CommandRegex().IsMatch(Command))
            return "A command is letters, digits and + - * _ only (like ct, moveToCell, aura+).";
        if (Find.Length > MaxFindLength || Replace.Length > MaxReplaceLength)
            return $"Find is at most {MaxFindLength} characters, Replace at most {MaxReplaceLength}.";
        if ((Find + Replace).IndexOfAny(['\0', '\r', '\n']) >= 0)
            return "Find and Replace are one line each.";
        if (Action == InterceptAction.Replace && Find.Length == 0)
            return "Replace needs the text to find.";
        if (Action == InterceptAction.Block && Command.Length == 0 && Find.Length == 0)
            return "A block rule needs a command or a text to find (it would drop everything).";
        if (Action == InterceptAction.Replace && Find == Replace)
            return "Find and Replace are the same.";
        return null;
    }

    public string DirectionText => Direction switch
    {
        InterceptDirection.Outbound => "out",
        InterceptDirection.Inbound => "in",
        _ => "in + out",
    };

    public string Summary => (Command.Length == 0 ? "any command" : Command)
        + (Find.Length == 0 ? string.Empty : $" containing \"{Find}\"")
        + (Action == InterceptAction.Block ? ": block" : $": replace with \"{Replace}\"");

    [GeneratedRegex(@"^[A-Za-z0-9_+\-*]{1,40}$")]
    private static partial Regex CommandRegex();
}

/// <summary>One message a rule changed or dropped.</summary>
/// <param name="At">When.</param>
/// <param name="Outbound">Client to server.</param>
/// <param name="Rules">The names of the rules that matched.</param>
/// <param name="Before">The message as it came (scrubbed, shortened).</param>
/// <param name="After">What was sent or applied (scrubbed, shortened); null when dropped.</param>
/// <param name="Categories">The desktop interceptor's categories of the message.</param>
public sealed record InterceptLogEntry(DateTimeOffset At, bool Outbound, string Rules, string Before, string? After, string Categories)
{
    public bool Blocked => After is null;

    /// <summary>The desktop log's direction column: OUT, IN or BLOCK.</summary>
    public string Direction => Blocked ? "BLOCK" : Outbound ? "OUT" : "IN";

    public string Header => $"{At.ToLocalTime():HH:mm:ss.fff} {(Outbound ? "->" : "<-")} {Direction} · {Rules} · {Categories}";
}

/// <summary>
/// The engine's packet interceptor (desktop: the capture proxy's interceptors and the Packet Interceptor window): a
/// list of rules applied in order to game messages of the live connection. Off by default and after every start of the
/// app; while off it costs one check per message. What it can never see or touch (the handshake, the token, the
/// login, the connection's safety messages) is enforced by <see cref="AqwConnection"/>; see <see cref="IPacketInterceptor"/>.
/// Thread-safe: rules are swapped as a whole; called on the connection's reader and writer threads.
/// </summary>
public sealed class PacketInterception : IPacketInterceptor
{
    public const int MaxRules = 30;

    public const int LogCapacity = 300;

    private const int MaxShownChars = 600;

    private readonly Func<string, string> _scrub;
    private readonly TimeProvider _time;
    private readonly RingBuffer<InterceptLogEntry> _log = new(LogCapacity);
    private volatile InterceptRule[] _rules = [];
    private volatile bool _enabled;
    private volatile bool _active;
    private long _seen;
    private long _changed;
    private long _blocked;
    private long _version;

    /// <param name="scrub">Applied to every logged message (the engine's secret scrubber).</param>
    public PacketInterception(Func<string, string>? scrub = null, TimeProvider? time = null)
    {
        _scrub = scrub ?? (s => s);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A message was changed or dropped (reader or writer thread).</summary>
    public event EventHandler<InterceptLogEntry>? Logged;

    /// <summary>Raised when <see cref="Enabled"/> or the rules change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The master switch. Never saved: every start of the app begins with it off.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            UpdateActive();
        }
    }

    public IReadOnlyList<InterceptRule> Rules => _rules;

    /// <summary>On, with at least one enabled rule.</summary>
    public bool IsActive => _active;

    public long Seen => Interlocked.Read(ref _seen);

    public long Changed => Interlocked.Read(ref _changed);

    public long Blocked => Interlocked.Read(ref _blocked);

    /// <summary>Goes up with every log entry (cheap change check for the page).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>"off", or "on: 2 rules, 5 changed, 1 blocked".</summary>
    public string StatusText => !_enabled
        ? "off"
        : $"on: {_rules.Count(r => r.Enabled)} of {_rules.Length} rules enabled, {Seen} seen, {Changed} changed, {Blocked} blocked";

    /// <summary>Replaces the rules (validated; at most <see cref="MaxRules"/>).</summary>
    /// <exception cref="ArgumentException">A rule is invalid or there are too many.</exception>
    public void SetRules(IEnumerable<InterceptRule> rules)
    {
        InterceptRule[] list = rules.ToArray();
        if (list.Length > MaxRules)
            throw new ArgumentException($"At most {MaxRules} rules.");
        foreach (InterceptRule r in list)
        {
            if (r.Validate() is string why)
                throw new ArgumentException($"{r.Name}: {why}");
        }
        _rules = list;
        UpdateActive();
    }

    public IReadOnlyList<InterceptLogEntry> GetLog() => _log.Snapshot();

    public void ClearLog()
    {
        _log.Clear();
        Interlocked.Increment(ref _version);
    }

    private void UpdateActive()
    {
        _active = _enabled && _rules.Any(r => r.Enabled);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Intercept(InterceptedFrame frame)
    {
        InterceptRule[] rules = _rules;
        if (!_enabled || rules.Length == 0)
            return;
        Interlocked.Increment(ref _seen);
        string[] commands = Commands(frame);
        List<string>? hits = null;
        foreach (InterceptRule rule in rules)
        {
            if (!rule.Matches(frame.Outbound, frame.Text, commands))
                continue;
            (hits ??= new()).Add(rule.Name);
            if (rule.Action == InterceptAction.Block)
            {
                frame.Blocked = true;
                break;
            }
            frame.Text = frame.Text.Replace(rule.Find, rule.Replace, StringComparison.Ordinal);
        }
        if (hits is null || !frame.Changed)
            return;
        if (frame.Blocked)
            Interlocked.Increment(ref _blocked);
        else
            Interlocked.Increment(ref _changed);
        InterceptLogEntry entry = new(
            _time.GetUtcNow(),
            frame.Outbound,
            string.Join(", ", hits),
            Shown(frame.Original),
            frame.Blocked ? null : Shown(frame.Text),
            string.Join(", ", PacketCategories.Of(commands)));
        _log.Add(entry);
        Interlocked.Increment(ref _version);
        try
        {
            Logged?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // A subscriber must never break the connection's loops.
        }
    }

    private static string[] Commands(InterceptedFrame frame)
    {
        string[] parsed = DesktopPacketCommands.Parse(frame.Text);
        if (frame.Command is string c && Array.IndexOf(parsed, c) < 0)
            return [c, .. parsed];
        return parsed;
    }

    private string Shown(string text)
    {
        string clean = _scrub(text);
        return clean.Length > MaxShownChars ? string.Concat(clean.AsSpan(0, MaxShownChars), $"... ({clean.Length} chars)") : clean;
    }

    // ------------------------------------------------------------------ saved rules (tools.json)

    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(IEnumerable<InterceptRule> rules) => JsonSerializer.Serialize(rules.ToList(), s_json);

    /// <summary>Saved rules; invalid ones are left out, an unreadable text gives none.</summary>
    public static IReadOnlyList<InterceptRule> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            List<InterceptRule>? list = JsonSerializer.Deserialize<List<InterceptRule>>(json, s_json);
            return (list ?? []).Where(r => r is { Id: not null, Name: not null, Command: not null, Find: not null, Replace: not null } && r.Validate() is null).Take(MaxRules).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
