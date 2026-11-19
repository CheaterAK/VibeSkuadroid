using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Notifications;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// Discord webhook settings (the desktop's webhook options): the webhook URL (validated, then stored encrypted; the
/// field is cleared and the URL is never shown again), which events to send, whether cards name the character, and
/// a test button.
/// </summary>
public sealed partial class DiscordWebhookViewModel : ObservableObject
{
    private readonly NotificationHub _hub;
    private bool _loading;

    public DiscordWebhookViewModel(NotificationHub hub)
    {
        _hub = hub;
        Load(hub.Settings.Current);
    }

    /// <summary>The URL being entered (write-only from the user's side: cleared once stored).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveUrlCommand))]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(SendTestCommand), nameof(ForgetUrlCommand))]
    private bool _hasUrl;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _notifyStarted;

    [ObservableProperty]
    private bool _notifyStopped;

    [ObservableProperty]
    private bool _notifyCrashed;

    [ObservableProperty]
    private bool _notifyRelogged;

    [ObservableProperty]
    private bool _notifySchedulerPaused;

    [ObservableProperty]
    private bool _notifyItemDrops;

    [ObservableProperty]
    private bool _includeCharacterName;

    [ObservableProperty]
    private bool _includeMap;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestCommand))]
    private bool _isTesting;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Secure storage exists (otherwise the URL cannot be kept).</summary>
    public bool CanStore => _hub.Vault.IsAvailable;

    public string StatusText => HasUrl
        ? $"A webhook is stored (encrypted). {(_hub.Sender.Url is DiscordWebhookUrl url ? url.Display : "It could not be read right now.")}"
        : "No webhook stored.";

    private void Load(NotificationSettings s)
    {
        _loading = true;
        HasUrl = _hub.Vault.HasUrl;
        Enabled = s.WebhookEnabled;
        NotifyStarted = s.WebhookNotifyStarted;
        NotifyStopped = s.WebhookNotifyStopped;
        NotifyCrashed = s.WebhookNotifyCrashed;
        NotifyRelogged = s.WebhookNotifyRelogged;
        NotifySchedulerPaused = s.WebhookNotifySchedulerPaused;
        NotifyItemDrops = s.WebhookNotifyItemDrops;
        IncludeCharacterName = s.WebhookIncludeCharacterName;
        IncludeMap = s.WebhookIncludeMap;
        _loading = false;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(UrlInput);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveUrl()
    {
        bool ok = _hub.SetWebhookUrl(UrlInput, out string error);
        // Cleared either way: the field never keeps a credential around.
        UrlInput = string.Empty;
        if (!ok)
        {
            Show(error, isError: true);
            return;
        }
        HasUrl = true;
        OnPropertyChanged(nameof(StatusText));
        if (!Enabled)
            Enabled = true;
        Show("Webhook stored. Use Send test to check it.", isError: false);
    }

    [RelayCommand(CanExecute = nameof(HasUrl))]
    private void ForgetUrl()
    {
        _hub.ForgetWebhook();
        HasUrl = false;
        Show("The webhook URL was deleted.", isError: false);
    }

    private bool CanTest() => HasUrl && !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task SendTestAsync()
    {
        if (_hub.Sender.Url is null)
            _hub.LoadWebhookUrl();
        IsTesting = true;
        Show("Sending...", isError: false);
        try
        {
            WebhookResult result = await _hub.SendWebhookTestAsync();
            Show(result.Outcome switch
            {
                WebhookOutcome.Sent => "Test card sent. Check the Discord channel.",
                WebhookOutcome.NoUrl => "The stored webhook could not be read; enter it again.",
                _ => result.Message,
            }, result.Outcome != WebhookOutcome.Sent);
        }
        finally
        {
            IsTesting = false;
        }
    }

    partial void OnEnabledChanged(bool value) => Save(s => s with { WebhookEnabled = value });

    partial void OnNotifyStartedChanged(bool value) => Save(s => s with { WebhookNotifyStarted = value });

    partial void OnNotifyStoppedChanged(bool value) => Save(s => s with { WebhookNotifyStopped = value });

    partial void OnNotifyCrashedChanged(bool value) => Save(s => s with { WebhookNotifyCrashed = value });

    partial void OnNotifyReloggedChanged(bool value) => Save(s => s with { WebhookNotifyRelogged = value });

    partial void OnNotifySchedulerPausedChanged(bool value) => Save(s => s with { WebhookNotifySchedulerPaused = value });

    partial void OnNotifyItemDropsChanged(bool value) => Save(s => s with { WebhookNotifyItemDrops = value });

    partial void OnIncludeCharacterNameChanged(bool value) => Save(s => s with { WebhookIncludeCharacterName = value });

    partial void OnIncludeMapChanged(bool value) => Save(s => s with { WebhookIncludeMap = value });

    private void Save(Func<NotificationSettings, NotificationSettings> change)
    {
        if (_loading)
            return;
        try
        {
            _hub.Settings.Update(change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Could not save: {e.Message}", isError: true);
        }
    }

    private void Show(string text, bool isError)
    {
        Message = text;
        IsError = isError;
    }
}
