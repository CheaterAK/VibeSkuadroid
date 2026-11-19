using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Plugins.Butler;

/// <summary>
/// ButlerV5's in-plugin log (its DebugLog) for the app: a capped in-memory buffer where a repeat of the previous line
/// raises that line's counter instead of adding a new one. No log files: the app's Console has the engine log, and the
/// page can share this one.
/// </summary>
public sealed class ButlerLog
{
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly List<ButlerLogLine> _lines = new();
    private long _seq;

    /// <summary>Diagnostic lines (the desktop "Enable debug logs" option); main events are always kept.</summary>
    public Func<bool> DebugEnabled { get; set; } = () => true;

    /// <summary>A line was added or the log cleared.</summary>
    public event EventHandler? Changed;

    /// <summary>Also told every main event (the backend forwards them to Skua's log, as the desktop plugin does).</summary>
    public Action<string>? EventSink { get; set; }

    /// <summary>A main event (always kept).</summary>
    public void Event(string text, ButlerLogLevel level = ButlerLogLevel.Info)
    {
        Add("ButlerV5", text, level);
        try
        {
            EventSink?.Invoke(text);
        }
        catch
        {
        }
    }

    /// <summary>A diagnostic line (only with debug logs on).</summary>
    public void Debug(string component, string text, ButlerLogLevel level = ButlerLogLevel.Info)
    {
        if (DebugEnabled())
            Add(component, text, level);
    }

    public IReadOnlyList<ButlerLogLine> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _lines.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(string component, string text, ButlerLogLevel level)
    {
        lock (_gate)
        {
            if (_lines.Count > 0 && _lines[^1] is { } last && last.Component == component && last.Text == text)
            {
                _lines[^1] = last with { At = DateTimeOffset.UtcNow, Count = last.Count + 1 };
            }
            else
            {
                _lines.Add(new ButlerLogLine(++_seq, DateTimeOffset.UtcNow, level, component, text, 1));
                if (_lines.Count > Capacity)
                    _lines.RemoveRange(0, _lines.Count - Capacity);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
