using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Packets;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The desktop's Packet Spammer (PacketSpammerViewModel): a list of packets sent in turn every interval, Start/Stop,
/// add, remove, clear, send once. Android adds the limits of <see cref="PacketSpammer"/> (minimum interval from the
/// game's pacing, a cap per run, automatic stop on a disconnect or a "slow down" warning), a confirmation with the
/// ban warning before each run, and keeps the list in <c>tools.json</c>. The desktop's "send to client" (fake server
/// packets for the local client) is left out: there is no client to show them.
/// </summary>
public sealed partial class PacketSpammerViewModel : ObservableObject
{
    public const string WarningText =
        "Spamming packets is not something the game client does: the server can flag or ban the account for it. Keep the list short and the interval long, and stop when you are done.";

    /// <summary>The tools store's entry for the list and its options.</summary>
    public const string StoreId = "packet-spammer";

    /// <summary>The most packets the list holds.</summary>
    public const int MaxPackets = 50;

    private readonly EngineHost _host;
    private readonly Platform.IUiDispatcher _dispatcher;
    private readonly PluginStore _store;
    private readonly PacketSpammer _spammer;
    private CancellationTokenSource? _run;
    private bool _loading;

    public PacketSpammerViewModel(AppServices services, PluginStore store, SpammerLimits? limits = null)
    {
        _host = services.Host;
        _dispatcher = services.Dispatcher;
        _store = store;
        _spammer = new PacketSpammer(services.Host.Engine, limits);
        Load();
    }

    public ConfirmHost Confirm { get; } = new();

    public ObservableCollection<string> Packets { get; } = new();

    public string Warning => WarningText;

    [ObservableProperty]
    private string _packetText = string.Empty;

    /// <summary>Delay between two packets in ms (clamped to <see cref="MinimumIntervalMs"/> when a run starts).</summary>
    [ObservableProperty]
    private decimal? _intervalMs = 1000;

    /// <summary>The most packets one run sends (1 to <see cref="MaxSendsLimit"/>).</summary>
    [ObservableProperty]
    private decimal? _maxSends = 100;

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private string _runStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendOnceCommand))]
    private bool _isOnline;

    [ObservableProperty]
    private string? _message;

    /// <summary>The shortest interval allowed now (the connection's pacing).</summary>
    public decimal MinimumIntervalMs => (decimal)_spammer.MinimumInterval.TotalMilliseconds;

    public decimal MaxSendsLimit => _spammer.Limits.MaxSendsPerRun;

    public string LimitsText => string.Create(CultureInfo.InvariantCulture,
        $"At least {MinimumIntervalMs:0} ms between packets (the game's pacing) and at most {MaxSendsLimit:0} per run. A run stops by itself on a disconnect, a dropped packet or a \"slow down\" warning.");

    public string ToggleText => IsRunning ? "Stop" : "Start";

    public bool HasPackets => Packets.Count > 0;

    /// <summary>The last run's result (tests).</summary>
    internal SpamResult? LastResult { get; private set; }

    /// <summary>The running loop (tests).</summary>
    internal Task<SpamResult>? RunTask { get; private set; }

    partial void OnIntervalMsChanged(decimal? value) => Save();

    partial void OnMaxSendsChanged(decimal? value) => Save();

    [RelayCommand]
    private void AddPacket()
    {
        Message = null;
        string text = PacketText.Trim();
        if (PacketSpammer.Validate(text) is string why)
        {
            Message = why;
            return;
        }
        if (Packets.Count >= MaxPackets)
        {
            Message = $"The list holds at most {MaxPackets} packets.";
            return;
        }
        Packets.Add(text);
        PacketText = string.Empty;
        Changed();
    }

    [RelayCommand]
    private void RemovePacket(string? packet)
    {
        int index = packet is null ? SelectedIndex : Packets.IndexOf(packet);
        if (index < 0 || index >= Packets.Count)
            return;
        Packets.RemoveAt(index);
        Changed();
    }

    [RelayCommand]
    private async Task ClearPacketsAsync()
    {
        if (Packets.Count == 0)
            return;
        if (!await Confirm.AskAsync("Clear the list", $"Remove all {Packets.Count} packets?", "Clear", danger: true))
            return;
        Stop();
        Packets.Clear();
        Changed();
    }

    private bool CanSendOnce() => IsOnline;

    /// <summary>The desktop's "Send now": the typed packet once, through the paced queue.</summary>
    [RelayCommand(CanExecute = nameof(CanSendOnce))]
    private async Task SendOnceAsync()
    {
        Message = null;
        string text = PacketText.Trim();
        if (PacketSpammer.Validate(text) is string why)
        {
            Message = why;
            return;
        }
        try
        {
            bool sent = await _host.Engine.SendPacketAsync(text);
            Message = sent ? "Sent once." : "The packet was not sent (queue full or disconnected).";
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRunning)
        {
            Stop();
            return;
        }
        await StartAsync();
    }

    /// <summary>Stops a run (the page's Stop, logout, the hub going away).</summary>
    public void Stop() => Interlocked.Exchange(ref _run, null)?.Cancel();

    private async Task StartAsync()
    {
        Message = null;
        if (Packets.Count == 0)
        {
            Message = "Add a packet first.";
            return;
        }
        if (!_host.Engine.Snapshot.IsOnline)
        {
            Message = "Log in first.";
            return;
        }
        TimeSpan requested = TimeSpan.FromMilliseconds((double)(IntervalMs ?? 0));
        TimeSpan every = _spammer.Clamp(requested);
        int max = (int)Math.Clamp(MaxSends ?? 1, 1, MaxSendsLimit);
        string text = string.Create(CultureInfo.InvariantCulture, $"{WarningText}\n\n{Packets.Count} packet(s), one every {every.TotalMilliseconds:0} ms, at most {max} sends.");
        if (requested < every)
            text += string.Create(CultureInfo.InvariantCulture, $" The interval was raised from {requested.TotalMilliseconds:0} ms to the minimum.");
        if (!await Confirm.AskAsync("Start the packet spammer?", text, "Start", danger: true))
            return;
        if (!await Confirm.AllowWhileScriptRunsAsync(_host.Engine.Snapshot.Script.IsBusy, "start the spammer"))
            return;
        if (IntervalMs < (decimal)every.TotalMilliseconds)
            IntervalMs = (decimal)every.TotalMilliseconds;
        MaxSends = max;

        CancellationTokenSource cts = new();
        if (Interlocked.CompareExchange(ref _run, cts, null) is not null)
            return;
        IsRunning = true;
        RunStatus = "Starting...";
        List<string> packets = Packets.ToList();
        PostedProgress progress = new(p => _dispatcher.Post(() =>
        {
            if (!IsRunning)
                return;
            SelectedIndex = p.Index;
            RunStatus = $"Sending {p.Index + 1} of {p.Count} · {p.Sent + 1}/{p.MaxSends}";
        }));
        RunTask = Task.Run(() => _spammer.RunAsync(packets, every, max, progress, cts.Token));
        SpamResult result;
        try
        {
            result = await RunTask.ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new SpamResult(SpamStopReason.Failed, 0, $"The spammer failed: {e.Message}");
        }
        Interlocked.CompareExchange(ref _run, null, cts);
        cts.Dispose();
        LastResult = result;
        _spammer.LogEnd(result);
        IsRunning = false;
        RunStatus = string.Empty;
        Message = result.Message;
    }

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        OnPropertyChanged(nameof(MinimumIntervalMs));
        OnPropertyChanged(nameof(LimitsText));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasPackets));
        Save();
    }

    private void Load()
    {
        _loading = true;
        if (_store.GetOption(StoreId, "packets") is string list)
        {
            foreach (string p in list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(MaxPackets))
            {
                if (PacketSpammer.Validate(p) is null)
                    Packets.Add(p);
            }
        }
        if (decimal.TryParse(_store.GetOption(StoreId, "interval"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal interval))
            IntervalMs = interval;
        if (decimal.TryParse(_store.GetOption(StoreId, "max"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal max))
            MaxSends = max;
        _loading = false;
    }

    private void Save()
    {
        if (_loading)
            return;
        try
        {
            _store.SetOptions(StoreId, new Dictionary<string, string>
            {
                ["packets"] = string.Join('\n', Packets),
                ["interval"] = (IntervalMs ?? 1000).ToString(CultureInfo.InvariantCulture),
                ["max"] = (MaxSends ?? 100).ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save the list: {e.Message}";
        }
    }

    private sealed class PostedProgress(Action<SpamProgress> report) : IProgress<SpamProgress>
    {
        public void Report(SpamProgress value) => report(value);
    }
}
