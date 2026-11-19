using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// Notify Drop (port of the desktop's NotifyDropViewModel): the drop watch list. Instead of the desktop's beeps a
/// watched drop posts an Android notification (its own channel; item names stay off a secure lock screen unless
/// allowed below). The list is saved (the desktop keeps it for the session only).
/// </summary>
public sealed partial class NotifyDropViewModel : ObservableObject
{
    private readonly NotificationHub _hub;
    private readonly IPlatformServices _platform;
    private bool _loading;

    public NotifyDropViewModel(NotificationHub hub, IPlatformServices platform)
    {
        _hub = hub;
        _platform = platform;
        Load(hub.Settings.Current);
    }

    /// <summary>The watched names.</summary>
    public ObservableCollection<string> NotifyDropList { get; } = new();

    /// <summary>One or more names separated by '|' (the desktop's input).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDropCommand))]
    private string _addDropInput = string.Empty;

    [ObservableProperty]
    private bool _notifyWatchedDrops;

    [ObservableProperty]
    private bool _notifyWatchedObtained;

    [ObservableProperty]
    private bool _showNamesOnLockScreen;

    [ObservableProperty]
    private bool _notificationsAllowed;

    [ObservableProperty]
    private string? _message;

    public bool IsEmpty => NotifyDropList.Count == 0;

    private void Load(NotificationSettings s)
    {
        _loading = true;
        NotifyDropList.Clear();
        foreach (string name in s.WatchedItems)
            NotifyDropList.Add(name);
        NotifyWatchedDrops = s.NotifyWatchedDrops;
        NotifyWatchedObtained = s.NotifyWatchedObtained;
        ShowNamesOnLockScreen = s.ShowNamesOnLockScreen;
        _loading = false;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshPlatformState();
    }

    /// <summary>Re-reads whether notifications are allowed (the user may have changed it).</summary>
    public void RefreshPlatformState() => NotificationsAllowed = _platform.NotificationsAllowed && _hub.Alerts.Enabled;

    private bool CanAdd() => !string.IsNullOrWhiteSpace(AddDropInput);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddDrop()
    {
        Save(s => s with { WatchedItems = DropWatchList.Add(s.WatchedItems, AddDropInput) });
        AddDropInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveDrop(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        Save(s => s with { WatchedItems = s.WatchedItems.Where(w => !string.Equals(w, name, StringComparison.OrdinalIgnoreCase)).ToList() });
    }

    [RelayCommand]
    private void RemoveAllDrops() => Save(s => s with { WatchedItems = [] });

    [RelayCommand]
    private void TestNotification()
    {
        RefreshPlatformState();
        if (!NotificationsAllowed)
        {
            Message = "Notifications are off for this app: allow them first.";
            return;
        }
        _hub.PostTestAlert();
        Message = "A test notification was posted.";
    }

    [RelayCommand]
    private void RequestPermission() => _platform.RequestNotificationPermission();

    partial void OnNotifyWatchedDropsChanged(bool value) => Save(s => s with { NotifyWatchedDrops = value });

    partial void OnNotifyWatchedObtainedChanged(bool value) => Save(s => s with { NotifyWatchedObtained = value });

    partial void OnShowNamesOnLockScreenChanged(bool value) => Save(s => s with { ShowNamesOnLockScreen = value });

    private void Save(Func<NotificationSettings, NotificationSettings> change)
    {
        if (_loading)
            return;
        try
        {
            NotificationSettings saved = _hub.Settings.Update(change);
            if (!saved.WatchedItems.SequenceEqual(NotifyDropList))
            {
                NotifyDropList.Clear();
                foreach (string name in saved.WatchedItems)
                    NotifyDropList.Add(name);
                OnPropertyChanged(nameof(IsEmpty));
            }
            Message = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save: {e.Message}";
        }
    }
}
