using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.GameView;
using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The Game view card (Dashboard and Control page): opens the real game in place of the headless session, asks first
/// when a script or the queue runs, shows where the account is, offers the stopped script again afterwards, and
/// edits the view's settings. One instance per process, shared by both pages.
/// </summary>
public sealed partial class GameViewViewModel : ObservableObject, IDisposable
{
    private readonly GameViewSwitch? _switch;
    private readonly IUiDispatcher _dispatcher;
    private bool _loading;

    public GameViewViewModel(AppServices services)
    {
        _switch = services.GameView;
        _dispatcher = services.Dispatcher;
        if (_switch is not null)
        {
            _switch.Changed += OnChanged;
            Apply(_switch.Status);
            LoadOptions(_switch.Options);
        }
    }

    /// <summary>The platform has a game view (Android); the desktop preview and tests without one hide the card.</summary>
    public bool IsAvailable => _switch is not null;

    /// <summary>The question before opening while a script or the queue runs.</summary>
    public ConfirmHost Confirm { get; } = new();

    public static IReadOnlyList<string> FrameRateChoices { get; } = GameViewOptions.FrameRates.Select(f => f.ToString(CultureInfo.InvariantCulture)).ToList();

    public static IReadOnlyList<string> RendererChoices { get; } = GameViewOptions.Renderers;

    public static IReadOnlyList<string> PixelRatioChoices { get; } = GameViewOptions.PixelRatios;

    public static IReadOnlyList<string> QualityChoices { get; } = GameViewOptions.Qualities;

    public decimal MinMemoryLimit => GameViewOptions.MinMemoryLimitMb;

    public decimal MaxMemoryLimit => GameViewOptions.MaxMemoryLimitMb;

    /// <summary>The game view has the account (or is opening or closing).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ReturnToHeadlessCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Opens the real game (Ruffle). The bot logs out while it is open: AQW allows one session per account.";

    [ObservableProperty]
    private string? _notice;

    /// <summary>"Start Gold farm again" after the switch stopped it.</summary>
    [ObservableProperty]
    private string? _restartScript;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private bool _showOptions;

    [ObservableProperty]
    private string _frameRate = "4";

    [ObservableProperty]
    private string _renderer = "webgl";

    [ObservableProperty]
    private string _pixelRatio = "1";

    [ObservableProperty]
    private string _quality = "low";

    [ObservableProperty]
    private bool _hidePlayers = true;

    [ObservableProperty]
    private bool _disableFX = true;

    [ObservableProperty]
    private bool _killLag;

    [ObservableProperty]
    private bool _mute = true;

    [ObservableProperty]
    private bool _showHud;

    [ObservableProperty]
    private decimal? _memoryLimitMb = GameViewOptions.DefaultMemoryLimitMb;

    public bool HasRestartOffer => RestartScript is not null;

    partial void OnRestartScriptChanged(string? value) => OnPropertyChanged(nameof(HasRestartOffer));

    private bool CanOpen() => IsAvailable && !IsBusy && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (_switch is null)
            return;
        Message = null;
        GameViewPlan plan = _switch.Plan();
        if (plan.NeedsConfirmation
            && !await Confirm.AskAsync("Open the game view?", plan.Question, "Open game view", danger: true).ConfigureAwait(true))
            return;
        IsWorking = true;
        try
        {
            await _switch.OpenAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        catch (OperationCanceledException)
        {
            Message = "The game view was not opened.";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void ReturnToHeadless() => _switch?.RequestClose("closed from the app");

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (_switch is null)
            return;
        Message = null;
        try
        {
            await _switch.RestartScriptAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        Message = null;
        _switch?.Dismiss();
    }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    /// <summary>System back: closes the question first.</summary>
    public bool CloseOverlay() => Confirm.CancelIfOpen();

    private void OnChanged(object? sender, GameViewStatus status) => _dispatcher.Post(() => Apply(status));

    private void Apply(GameViewStatus s)
    {
        IsBusy = s.Busy;
        StatusText = s.Busy && s.Text.Length > 0
            ? s.Text
            : "Opens the real game (Ruffle). The bot logs out while it is open: AQW allows one session per account.";
        Notice = s.Notice;
        RestartScript = s.RestartScript is string path ? Path.GetFileNameWithoutExtension(path) : null;
    }

    private void LoadOptions(GameViewOptions o)
    {
        _loading = true;
        try
        {
            FrameRate = o.FrameRate.ToString(CultureInfo.InvariantCulture);
            Renderer = o.Renderer;
            PixelRatio = o.PixelRatio;
            Quality = o.Quality;
            HidePlayers = o.HidePlayers;
            DisableFX = o.DisableFX;
            KillLag = o.KillLag;
            Mute = o.Mute;
            ShowHud = o.ShowHud;
            MemoryLimitMb = o.MemoryLimitMb;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveOptions()
    {
        if (_loading || _switch is null)
            return;
        GameViewOptions current = _switch.Options;
        _switch.SetOptions(current with
        {
            FrameRate = int.TryParse(FrameRate, NumberStyles.None, CultureInfo.InvariantCulture, out int fps) ? fps : current.FrameRate,
            Renderer = Renderer,
            PixelRatio = PixelRatio,
            Quality = Quality,
            HidePlayers = HidePlayers,
            DisableFX = DisableFX,
            KillLag = KillLag,
            Mute = Mute,
            ShowHud = ShowHud,
            MemoryLimitMb = MemoryLimitMb is decimal mb ? (int)mb : current.MemoryLimitMb,
        });
    }

    partial void OnFrameRateChanged(string value) => SaveOptions();

    partial void OnRendererChanged(string value) => SaveOptions();

    partial void OnPixelRatioChanged(string value) => SaveOptions();

    partial void OnQualityChanged(string value) => SaveOptions();

    partial void OnHidePlayersChanged(bool value) => SaveOptions();

    partial void OnDisableFXChanged(bool value) => SaveOptions();

    partial void OnKillLagChanged(bool value) => SaveOptions();

    partial void OnMuteChanged(bool value) => SaveOptions();

    partial void OnShowHudChanged(bool value) => SaveOptions();

    partial void OnMemoryLimitMbChanged(decimal? value) => SaveOptions();

    public void Dispose()
    {
        if (_switch is not null)
            _switch.Changed -= OnChanged;
    }
}
