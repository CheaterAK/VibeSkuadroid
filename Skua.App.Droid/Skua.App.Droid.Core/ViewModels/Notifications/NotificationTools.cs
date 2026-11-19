using Skua.App.Droid.Core.Notifications;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The notification pages of the Tools hub: Notification Center, Notify Drop, Junk Items and Discord Webhook. Created
/// once (the shell outlives activities); each page is told when it is shown.
/// </summary>
public sealed class NotificationTools : IDisposable
{
    private readonly ToolsViewModel _tools;

    private NotificationTools(ToolsViewModel tools, AppServices services, NotificationHub hub)
    {
        _tools = tools;
        Center = new NotificationCenterViewModel(hub.History, services.Dispatcher, services.FilePicker, services.Host.Platform);
        NotifyDrop = new NotifyDropViewModel(hub, services.Host.Platform);
        Junk = new JunkItemsViewModel(hub, services.Host);
        Webhook = new DiscordWebhookViewModel(hub);

        tools.Entries.Add(new ToolEntry("Notification Center", "Drops, deaths, relogins, quests, maps and script starts, stops and errors, kept across restarts.",
            () => tools.Show(Center, "Notification Center")));
        tools.Entries.Add(new ToolEntry("Notify Drop", "A watch list of items: an Android notification when one drops or is obtained.",
            () => tools.Show(NotifyDrop, "Notify Drop")));
        tools.Entries.Add(new ToolEntry("Junk Items", "Mark items as junk, sell them, and reject junk drops automatically.",
            () => tools.Show(Junk, "Junk Items")));
        tools.Entries.Add(new ToolEntry("Discord Webhook", "Script, relogin and drop cards in a Discord channel.",
            () => tools.Show(Webhook, "Discord Webhook")));
        tools.PropertyChanged += OnToolsChanged;
    }

    public NotificationCenterViewModel Center { get; }

    public NotifyDropViewModel NotifyDrop { get; }

    public JunkItemsViewModel Junk { get; }

    public DiscordWebhookViewModel Webhook { get; }

    /// <summary>Adds the pages to <paramref name="tools"/>; null when the host has no notification hub.</summary>
    public static NotificationTools? AddTo(ToolsViewModel tools, AppServices services) =>
        services.Notifications is NotificationHub hub ? new NotificationTools(tools, services, hub) : null;

    /// <summary>Whether the Tools hub itself is on screen (the Settings tab shows it).</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Update();
        }
    }

    private bool _visible;

    private void OnToolsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolsViewModel.CurrentPage))
            Update();
    }

    private void Update()
    {
        object? page = _visible ? _tools.CurrentPage : null;
        Center.IsActive = ReferenceEquals(page, Center);
        Junk.IsActive = ReferenceEquals(page, Junk);
        if (ReferenceEquals(page, NotifyDrop))
            NotifyDrop.RefreshPlatformState();
    }

    public void Dispose()
    {
        _tools.PropertyChanged -= OnToolsChanged;
        Center.Dispose();
    }
}
