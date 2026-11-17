using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using System.Collections.Concurrent;
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

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly ConcurrentQueue<PacketEntry> _pendingPackets = new();
    private int _flushPosted;

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

    private void OnLogAdded(object? sender, LogEntry e)
    {
        _pendingLog.Enqueue(e);
        ScheduleFlush();
    }

    private void OnPacketLogged(object? sender, PacketEntry e)
    {
        _pendingPackets.Enqueue(e);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushPosted, 1) == 0)
            _services.Dispatcher.Post(Flush);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushPosted, 0);
        bool added = false;
        while (_pendingLog.TryDequeue(out LogEntry? e))
        {
            if (!ShowDebug && e.Level == EngineLogLevel.Debug)
                continue;
            LogLines.Add(e);
            added = true;
        }
        while (_pendingPackets.TryDequeue(out PacketEntry? p))
        {
            if (!MatchesFilter(p))
                continue;
            PacketLines.Add(p);
            added = true;
        }
        Trim(LogLines);
        Trim(PacketLines);
        if (added && IsActive)
            LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadLog()
    {
        LogLines.Clear();
        foreach (LogEntry e in _host.Engine.GetRecentLog().Where(e => ShowDebug || e.Level != EngineLogLevel.Debug).TakeLast(VisibleLines))
            LogLines.Add(e);
    }

    private void ReloadPackets()
    {
        PacketLines.Clear();
        foreach (PacketEntry p in _host.Engine.GetRecentPackets().Where(MatchesFilter).TakeLast(VisibleLines))
            PacketLines.Add(p);
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
