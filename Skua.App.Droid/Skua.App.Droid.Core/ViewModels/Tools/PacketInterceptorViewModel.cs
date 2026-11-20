using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Packets;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The desktop's Packet Interceptor (PacketInterceptorViewModel and the capture proxy's interceptors) on the headless
/// connection: rules that change or drop game messages (match by direction, command and text; replace or block),
/// each with its own switch, a master switch that is off at every start, and the log of the messages the rules
/// changed or dropped with the desktop's categories, search and export. The desktop's server picker and proxy
/// connect are not needed: the interceptor sits on the app's own connection.
/// </summary>
public sealed partial class PacketInterceptorViewModel : ObservableObject, IDisposable
{
    public const string WarningText =
        "Changing or dropping game messages makes the game state you see differ from the server's, can break scripts, and changed packets the server does not expect can get the account flagged. The login, the token and the connection's own safety messages are never touched.";

    /// <summary>The tools store's entry for the rules.</summary>
    public const string StoreId = "packet-interceptor";

    public static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly EngineHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly PluginStore _store;
    private CancellationTokenSource? _loop;
    private long _shownVersion = -1;

    public PacketInterceptorViewModel(AppServices services, PluginStore store)
    {
        _host = services.Host;
        _dispatcher = services.Dispatcher;
        _store = store;
        Interception = services.Host.Engine.Interception;
        if (Interception is not null)
        {
            try
            {
                Interception.SetRules(PacketInterception.Deserialize(_store.GetOption(StoreId, "rules")));
            }
            catch (ArgumentException)
            {
                // Saved rules that no longer validate are dropped.
            }
            Interception.StateChanged += OnStateChanged;
        }
        ShowRules();
        UpdateStatus();
    }

    /// <summary>The engine's interceptor; null when the engine has none (the page says so).</summary>
    public PacketInterception? Interception { get; }

    public bool IsAvailable => Interception is not null;

    public ConfirmHost Confirm { get; } = new();

    public string Warning => WarningText;

    public ObservableCollection<InterceptRule> Rules { get; } = new();

    public IReadOnlyList<InterceptDirection> Directions { get; } = Enum.GetValues<InterceptDirection>();

    public IReadOnlyList<InterceptAction> Actions { get; } = Enum.GetValues<InterceptAction>();

    /// <summary>Refreshes the log while true (page shown, app in the foreground). Set by the Tools hub.</summary>
    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isOn;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private IReadOnlyList<InterceptLogEntry> _log = [];

    [ObservableProperty]
    private string _logFilter = string.Empty;

    [ObservableProperty]
    private string _logSummary = string.Empty;

    // ------------------------------------------------------------------ rule editor

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    private bool _isEditing;

    private string? _editingId;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private InterceptDirection _editDirection = InterceptDirection.Outbound;

    [ObservableProperty]
    private string _editCommand = string.Empty;

    [ObservableProperty]
    private string _editFind = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    private InterceptAction _editAction = InterceptAction.Replace;

    [ObservableProperty]
    private string _editReplace = string.Empty;

    [ObservableProperty]
    private string? _editError;

    public bool IsReplace => EditAction == InterceptAction.Replace;

    public string ToggleText => IsOn ? "Turn off" : "Turn on";

    public bool HasRules => Rules.Count > 0;

    partial void OnVisibleChanged(bool value)
    {
        CancellationTokenSource? old = Interlocked.Exchange(ref _loop, null);
        old?.Cancel();
        old?.Dispose();
        if (!value || Interception is null)
            return;
        RefreshLog(force: true);
        CancellationTokenSource cts = new();
        _loop = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    partial void OnLogFilterChanged(string value) => RefreshLog(force: true);

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(LogRefreshInterval, token).ConfigureAwait(false);
                if (Interception is { } ic && ic.Version != Interlocked.Read(ref _shownVersion))
                    _dispatcher.Post(() => RefreshLog(force: false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Reads the log now (tests and the page opening).</summary>
    internal void RefreshLog(bool force)
    {
        if (Interception is not { } ic)
            return;
        long version = ic.Version;
        if (!force && version == Interlocked.Read(ref _shownVersion))
            return;
        Interlocked.Exchange(ref _shownVersion, version);
        IReadOnlyList<InterceptLogEntry> all = ic.GetLog();
        string f = LogFilter.Trim();
        Log = all.Reverse()
            .Where(e => f.Length == 0 || e.Before.Contains(f, StringComparison.OrdinalIgnoreCase) || (e.After?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.Rules.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Categories.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
        LogSummary = all.Count == 0 ? "Nothing changed or dropped yet." : $"{Log.Count} of {all.Count} shown, newest first.";
        UpdateStatus();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _dispatcher.Post(UpdateStatus);

    private void UpdateStatus()
    {
        IsOn = Interception?.Enabled ?? false;
        StatusText = Interception is null ? "Not available in this engine." : Interception.StatusText;
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Interception is not { } ic)
            return;
        Message = null;
        if (ic.Enabled)
        {
            ic.Enabled = false;
            UpdateStatus();
            return;
        }
        if (!ic.Rules.Any(r => r.Enabled))
        {
            Message = "Add or enable a rule first.";
            return;
        }
        if (!await Confirm.AskAsync("Turn the packet interceptor on?", $"{WarningText}\n\n{ic.Rules.Count(r => r.Enabled)} rule(s) apply from the next game message on, until you turn it off or the app restarts.", "Turn on", danger: true))
            return;
        ic.Enabled = true;
        UpdateStatus();
    }

    [RelayCommand]
    private void NewRule()
    {
        _editingId = null;
        EditName = $"Rule {Rules.Count + 1}";
        EditDirection = InterceptDirection.Outbound;
        EditCommand = string.Empty;
        EditFind = string.Empty;
        EditAction = InterceptAction.Replace;
        EditReplace = string.Empty;
        EditError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditRule(InterceptRule? rule)
    {
        if (rule is null)
            return;
        _editingId = rule.Id;
        EditName = rule.Name;
        EditDirection = rule.Direction;
        EditCommand = rule.Command;
        EditFind = rule.Find;
        EditAction = rule.Action;
        EditReplace = rule.Replace;
        EditError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditError = null;
    }

    [RelayCommand]
    private void SaveRule()
    {
        InterceptRule? existing = _editingId is null ? null : Rules.FirstOrDefault(r => r.Id == _editingId);
        InterceptRule rule = new(
            _editingId ?? Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(EditName) ? $"Rule {Rules.Count + 1}" : EditName.Trim(),
            existing?.Enabled ?? true,
            EditDirection,
            EditCommand.Trim(),
            EditFind,
            EditAction,
            EditAction == InterceptAction.Block ? string.Empty : EditReplace);
        if (rule.Validate() is string why)
        {
            EditError = why;
            return;
        }
        List<InterceptRule> list = Rules.ToList();
        int index = existing is null ? -1 : list.IndexOf(existing);
        if (index >= 0)
            list[index] = rule;
        else if (list.Count >= PacketInterception.MaxRules)
        {
            EditError = $"At most {PacketInterception.MaxRules} rules.";
            return;
        }
        else
            list.Add(rule);
        if (Apply(list))
            IsEditing = false;
    }

    [RelayCommand]
    private void ToggleRule(InterceptRule? rule)
    {
        if (rule is null)
            return;
        Apply(Rules.Select(r => r.Id == rule.Id ? r with { Enabled = !r.Enabled } : r).ToList());
    }

    [RelayCommand]
    private void MoveRuleUp(InterceptRule? rule)
    {
        List<InterceptRule> list = Rules.ToList();
        int index = rule is null ? -1 : list.FindIndex(r => r.Id == rule.Id);
        if (index <= 0)
            return;
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        Apply(list);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(InterceptRule? rule)
    {
        if (rule is null)
            return;
        if (!await Confirm.AskAsync("Delete the rule", $"Delete \"{rule.Name}\" ({rule.Summary})?", "Delete", danger: true))
            return;
        Apply(Rules.Where(r => r.Id != rule.Id).ToList());
    }

    [RelayCommand]
    private void ClearLog()
    {
        Interception?.ClearLog();
        RefreshLog(force: true);
    }

    /// <summary>The desktop's Export: one "[OUT] packet" line per logged message (after the change; dropped ones as BLOCK).</summary>
    [RelayCommand]
    private void ShareLog()
    {
        if (Interception is null)
            return;
        IReadOnlyList<InterceptLogEntry> all = Interception.GetLog();
        if (all.Count == 0)
        {
            Message = "The log is empty.";
            return;
        }
        string text = string.Join('\n', all.Select(e => $"[{e.Direction}] {(e.After ?? e.Before).Replace("\r", "\\r").Replace("\n", "\\n")}"));
        _host.Platform.ShareText("VibeSkua intercepted packets", Diagnostics.DiagnosticText.Scrub(text));
    }

    /// <summary>System back: closes the rule editor first. False when it was not open.</summary>
    public bool GoBack()
    {
        if (!IsEditing)
            return false;
        CancelEdit();
        return true;
    }

    private bool Apply(IReadOnlyList<InterceptRule> rules)
    {
        if (Interception is null)
            return false;
        try
        {
            Interception.SetRules(rules);
        }
        catch (ArgumentException e)
        {
            EditError = e.Message;
            return false;
        }
        ShowRules();
        UpdateStatus();
        try
        {
            _store.SetOption(StoreId, "rules", PacketInterception.Serialize(rules));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save the rules: {e.Message}";
        }
        return true;
    }

    private void ShowRules()
    {
        Rules.Clear();
        foreach (InterceptRule r in Interception?.Rules ?? [])
            Rules.Add(r);
        OnPropertyChanged(nameof(HasRules));
    }

    public void Dispose()
    {
        Visible = false;
        if (Interception is not null)
            Interception.StateChanged -= OnStateChanged;
    }
}
