using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using System.Collections.ObjectModel;
using System.Text;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>Log console (copy/share) and packet logger (capture toggle, filter, send raw).</summary>
public sealed partial class ConsoleViewModel : ObservableObject, IDisposable
{
    /// <summary>Lines kept in the on-screen lists (the engine keeps more for copy/share).</summary>
    public const int VisibleLines = 600;

    /// <summary>Android's binder limit makes huge share intents fail; share the tail only.</summary>
    public const int MaxShareChars = 200_000;

    /// <summary>
    /// Lines waiting for the UI thread at most. While the UI is hidden (app in the background) nothing is flushed into
    /// the on-screen lists; past this many the lists are rebuilt from the engine's buffer when the UI shows again.
    /// </summary>
    public const int PendingCapacity = 2 * VisibleLines;

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly BoundedFeed<LogEntry> _pendingLog = new(PendingCapacity);
    private readonly BoundedFeed<PacketEntry> _pendingPackets = new(PendingCapacity);
    private int _flushPosted;
    private volatile bool _uiVisible = true;

    // Entries a rebuild already took from the engine's buffer; the next flush skips them (they may also be queued).
    private HashSet<object>? _rebuilt;

    public ConsoleViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        foreach (LogEntry e in _host.Engine.GetRecentLog().TakeLast(VisibleLines))
            LogLines.Add(e);
        _host.Engine.LogAdded += OnLogAdded;
        _host.Engine.PacketLogged += OnPacketLogged;
        _captureEnabled = _host.Engine.PacketCaptureEnabled;
    }

    public ObservableCollection<LogEntry> LogLines { get; } = new();

    public ObservableCollection<PacketEntry> PacketLines { get; } = new();

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogTab))]
    private bool _isPacketsTab;

    [ObservableProperty]
    private bool _captureEnabled;

    [ObservableProperty]
    private string _packetFilter = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPacketCommand))]
    private string _rawPacket = string.Empty;

    [ObservableProperty]
    private bool _showDebug;

    [ObservableProperty]
    private string? _message;

    /// <summary>Raised after new lines were appended (the view scrolls to the end).</summary>
    public event EventHandler? LinesAppended;

    public bool IsLogTab => !IsPacketsTab;

    partial void OnCaptureEnabledChanged(bool value) => _host.Engine.PacketCaptureEnabled = value;

    partial void OnPacketFilterChanged(string value) => ReloadPackets();

    partial void OnShowDebugChanged(bool value) => ReloadLog();

    [RelayCommand]
    private void ShowLog() => IsPacketsTab = false;

    [RelayCommand]
    private void ShowPackets() => IsPacketsTab = true;

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        await _services.FilePicker.CopyTextAsync(FullLogText());
        Message = "Log copied.";
    }

    [RelayCommand]
    private void ShareLog() => _host.Platform.ShareText("VibeSkua log", Tail(FullLogText()));

    [RelayCommand]
    private void ClearLog()
    {
        _host.Engine.ClearLog();
        LogLines.Clear();
    }

    [RelayCommand]
    private async Task CopyPacketsAsync()
    {
        await _services.FilePicker.CopyTextAsync(string.Join('\n', PacketLines.Select(p => p.Display)));
        Message = "Packets copied.";
    }

    [RelayCommand]
    private void SharePackets() => _host.Platform.ShareText("VibeSkua packets", Tail(string.Join('\n', PacketLines.Select(p => p.Display))));

    [RelayCommand]
    private void ClearPackets()
    {
        _host.Engine.ClearPackets();
        PacketLines.Clear();
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(RawPacket);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendPacketAsync()
    {
        Message = null;
        try
        {
            bool sent = await _host.Engine.SendPacketAsync(RawPacket);
            Message = sent ? "Sent." : "Dropped (queue full or disconnected).";
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    /// <summary>The whole engine log as text (what copy and share use).</summary>
    public string FullLogText()
    {
        StringBuilder sb = new();
        sb.Append("VibeSkua Android ").Append(_host.Platform.AppVersion).Append(" on ").AppendLine(_host.Platform.DeviceDescription);
        foreach (LogEntry e in _host.Engine.GetRecentLog())
            sb.AppendLine(e.Display);
        return sb.ToString();
    }

    internal static string Tail(string text) =>
        text.Length <= MaxShareChars ? text : "...(truncated)\n" + text[^MaxShareChars..];

    /// <summary>
    /// Whether the UI is showing (the platform calls this when the app goes to the background and back). While hidden,
    /// new lines only wait in a bounded queue: nothing is added to the bound lists, so no layout or list work runs for
    /// a window that is not there, and coming back costs at most one rebuild of <see cref="VisibleLines"/> lines.
    /// </summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        if (visible)
            ScheduleFlush();
    }

    private void OnLogAdded(object? sender, LogEntry e)
    {
        _pendingLog.Add(e);
        ScheduleFlush();
    }

    private void OnPacketLogged(object? sender, PacketEntry e)
    {
        _pendingPackets.Add(e);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_uiVisible && Interlocked.Exchange(ref _flushPosted, 1) == 0)
            _services.Dispatcher.Post(Flush);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushPosted, 0);
        if (!_uiVisible)
            return; // stays queued (bounded) until the UI shows again
        HashSet<object>? rebuilt = _rebuilt;
        _rebuilt = null;

        bool added = false;
        (IReadOnlyList<LogEntry> logs, bool logOverflow) = _pendingLog.Drain();
        if (logOverflow)
        {
            ReloadLog();
            added = true;
        }
        else
        {
            added |= Append(LogLines, logs.Where(e => (ShowDebug || e.Level != EngineLogLevel.Debug) && rebuilt?.Contains(e) != true));
        }

        (IReadOnlyList<PacketEntry> packets, bool packetOverflow) = _pendingPackets.Drain();
        if (packetOverflow)
        {
            ReloadPackets();
            added = true;
        }
        else
        {
            added |= Append(PacketLines, packets.Where(p => MatchesFilter(p) && rebuilt?.Contains(p) != true));
        }

        if (added && IsActive)
            LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends the newest lines, keeping at most <see cref="VisibleLines"/>; a burst larger than that replaces the list.</summary>
    private static bool Append<T>(ObservableCollection<T> lines, IEnumerable<T> items)
    {
        List<T> batch = items.ToList();
        if (batch.Count == 0)
            return false;
        if (batch.Count >= VisibleLines)
        {
            lines.Clear();
            batch = batch.GetRange(batch.Count - VisibleLines, VisibleLines);
        }
        foreach (T item in batch)
            lines.Add(item);
        Trim(lines);
        return true;
    }

    private void ReloadLog()
    {
        LogLines.Clear();
        foreach (LogEntry e in _host.Engine.GetRecentLog().Where(e => ShowDebug || e.Level != EngineLogLevel.Debug).TakeLast(VisibleLines))
            LogLines.Add(e);
        Remember(LogLines);
    }

    private void ReloadPackets()
    {
        PacketLines.Clear();
        foreach (PacketEntry p in _host.Engine.GetRecentPackets().Where(MatchesFilter).TakeLast(VisibleLines))
            PacketLines.Add(p);
        Remember(PacketLines);
    }

    // The newest lines of a rebuild may still be queued (added to the engine's buffer just before the rebuild read it).
    private void Remember<T>(ObservableCollection<T> lines)
        where T : class
    {
        _rebuilt ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = Math.Max(0, lines.Count - 64); i < lines.Count; i++)
            _rebuilt.Add(lines[i]);
    }

    private bool MatchesFilter(PacketEntry p) =>
        PacketFilter.Length == 0 || p.Text.Contains(PacketFilter, StringComparison.OrdinalIgnoreCase);

    private static void Trim<T>(ObservableCollection<T> lines)
    {
        while (lines.Count > VisibleLines)
            lines.RemoveAt(0);
    }

    public void Dispose()
    {
        _host.Engine.LogAdded -= OnLogAdded;
        _host.Engine.PacketLogged -= OnPacketLogged;
    }
}
