using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.Core.Interfaces;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// Diagnostics (desktop: DiagnosticsView, RuntimeHelpersView and the logs): engine state, the connection (server,
/// latency, packets per minute), memory and the watchdog, the Flash bridge's gap log, the process exits Android
/// reported, Skua's runtime helpers and events, and a scrubbed bundle to share. Read on a worker thread every
/// <see cref="RefreshInterval"/> while the page is shown and the app is in the foreground; idle otherwise.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>Lines of the events log and of the engine log a bundle carries.</summary>
    public const int BundleLogLines = 150;

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly PacketRateMeter _rate = new();
    private readonly object _rateLock = new();
    private CancellationTokenSource? _loop;
    private DiagnosticsReading? _last;

    /// <summary>Reads done so far (tests).</summary>
    internal int Refreshes;

    public DiagnosticsViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
    }

    /// <summary>Shown and in the foreground: refresh every second. Set by the Tools hub.</summary>
    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _engineRows = [];

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _connectionRows = [];

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _memoryRows = [];

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _runtimeRows = [];

    [ObservableProperty]
    private IReadOnlyList<FlashGapRow> _flashGaps = [];

    [ObservableProperty]
    private string _flashGapsText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ExitRow> _exits = [];

    [ObservableProperty]
    private string _exitsText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _skuaEvents = [];

    /// <summary>Skua's own diagnostics recorder (script starts and stops, waits, relogins; samples every 5 s while on).</summary>
    [ObservableProperty]
    private bool _recordSkuaEvents;

    [ObservableProperty]
    private bool _canRecordSkuaEvents;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand), nameof(CopyCommand))]
    private bool _isBusy;

    private bool _applying;

    partial void OnVisibleChanged(bool value)
    {
        CancellationTokenSource? old = Interlocked.Exchange(ref _loop, null);
        old?.Cancel();
        old?.Dispose();
        if (!value)
            return;
        CancellationTokenSource cts = new();
        _loop = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    partial void OnRecordSkuaEventsChanged(bool value)
    {
        if (_applying)
            return;
        if (_host.Engine.SkuaServices?.GetService(typeof(IDiagnosticsService)) is IDiagnosticsService skua)
            skua.SetEnabled(value);
        _ = RefreshAsync();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                DiagnosticsReading reading = Read();
                _services.Dispatcher.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        Show(reading);
                });
                await Task.Delay(RefreshInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _services.Dispatcher.Post(() => Message = $"Diagnostics stopped refreshing: {e.GetType().Name}: {DiagnosticText.Scrub(e.Message)}");
        }
    }

    /// <summary>Reads now (the Refresh button, tests).</summary>
    [RelayCommand]
    internal async Task RefreshAsync()
    {
        DiagnosticsReading reading = await Task.Run(Read).ConfigureAwait(true);
        Show(reading);
    }

    private DiagnosticsReading Read()
    {
        IBotEngine engine = _host.Engine;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EngineSnapshot snapshot = engine.Snapshot;
        ConnectionDetails? connection = engine.GetConnectionDetails();
        PacketRate? rate;
        lock (_rateLock)
            rate = _rate.Add(now, connection);
        Settings.AppSettings settings = _host.Settings.Current;
        MemoryReading memory = DiagnosticsReport.ReadMemory(snapshot.Memory, settings.MemoryThresholdMb, settings.AutoRestartOnMemory, VerdictText(_host.MemoryVerdict));
        DiagnosticsStore store = new(_services.Paths.DiagnosticsDir);
        IReadOnlyList<ExitRow> exits = DiagnosticsReport.Exits(store.ReadLogTail(400));
        RuntimeReading? runtime = null;
        try
        {
            runtime = DiagnosticsReport.ReadRuntime(engine.SkuaServices);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Skua's helpers read the game; a failed read only leaves the section empty.
        }
        Interlocked.Increment(ref Refreshes);
        return new DiagnosticsReading(now, snapshot, connection, rate, memory, engine.GetFlashGaps(), exits, store.HasPendingReport, runtime);
    }

    private static string VerdictText(Memory.MemoryVerdict verdict) => verdict switch
    {
        Memory.MemoryVerdict.RestartPending => "over the threshold, restart pending",
        Memory.MemoryVerdict.RestartNow => "restarting",
        Memory.MemoryVerdict.ThresholdTooLow => "threshold below what the app needs at start",
        _ => verdict.ToString().ToLowerInvariant(),
    };

    private string? InterceptionText() => null;

    private void Show(DiagnosticsReading r)
    {
        _last = r;
        EngineRows = DiagnosticsReport.EngineRows(r.Engine, _host.Engine.PacketCaptureEnabled, InterceptionText());
        ConnectionRows = DiagnosticsReport.ConnectionRows(r.Connection, r.Rate, r.At);
        MemoryRows = DiagnosticsReport.MemoryRows(r.Memory);
        RuntimeRows = DiagnosticsReport.RuntimeRows(r.Runtime);
        if (r.FlashGaps is null)
        {
            FlashGaps = [];
            FlashGapsText = "Not available: no Skua host in this build.";
        }
        else
        {
            if (!FlashGaps.SequenceEqual(r.FlashGaps))
                FlashGaps = r.FlashGaps;
            FlashGapsText = r.FlashGaps.Count == 0
                ? "None: every call of Skua had an answer."
                : $"{r.FlashGaps.Count} distinct calls, {r.FlashGaps.Sum(g => g.Count).ToString("N0", CultureInfo.InvariantCulture)} in all. Missing: not implemented; Impossible: needs Flash; Approximated: answered by reasoning.";
        }
        if (!Exits.SequenceEqual(r.Exits))
            Exits = r.Exits;
        ExitsText = (r.PendingCrashReport ? "A crash report waits for the next start. " : string.Empty)
            + (r.Exits.Count == 0 ? "No exit recorded by Android (it keeps them on Android 11 and later)." : $"The last {r.Exits.Count} exits Android recorded, newest first.");
        _applying = true;
        CanRecordSkuaEvents = r.Runtime is not null;
        RecordSkuaEvents = r.Runtime?.SkuaEventsEnabled ?? false;
        _applying = false;
        SkuaEvents = r.Runtime?.SkuaEvents.Select(DiagnosticsReport.EventRow).ToList() ?? [];
        UpdatedText = $"Updated {r.At.ToLocalTime():HH:mm:ss}";
    }

    private bool CanShare() => !IsBusy;

    /// <summary>The share bundle (scrubbed), built on a worker thread.</summary>
    internal async Task<string> BuildBundleAsync()
    {
        DiagnosticsReading reading = _last ?? await Task.Run(Read).ConfigureAwait(true);
        IBotEngine engine = _host.Engine;
        bool capture = engine.PacketCaptureEnabled;
        string? interception = InterceptionText();
        string[] personal = [_host.Settings.Current.AccountName, _host.Settings.Current.RememberedAccount, reading.Engine.AccountName, reading.Engine.Character?.Name];
        return await Task.Run(() =>
        {
            IReadOnlyList<string> events = new DiagnosticsStore(_services.Paths.DiagnosticsDir).ReadLogTail(BundleLogLines);
            IReadOnlyList<LogEntry> log = engine.GetRecentLog();
            return DiagnosticsReport.Bundle(reading, _host.Platform.AppVersion, _host.Platform.DeviceDescription,
                log.Skip(Math.Max(0, log.Count - BundleLogLines)), events, capture, interception, DiagnosticText.Secrets, personal);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanShare))]
    private async Task ShareAsync()
    {
        IsBusy = true;
        try
        {
            _host.Platform.ShareText("VibeSkua diagnostics", await BuildBundleAsync());
            Message = "Shared. Passwords, tokens, e-mail addresses, webhook URLs and the account name were removed.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Message = $"Could not build the bundle: {DiagnosticText.Scrub(e.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanShare))]
    private async Task CopyAsync()
    {
        IsBusy = true;
        try
        {
            await _services.FilePicker.CopyTextAsync(await BuildBundleAsync());
            Message = "Copied (scrubbed).";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Message = $"Could not build the bundle: {DiagnosticText.Scrub(e.Message)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose() => Visible = false;
}
