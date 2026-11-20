using System.ComponentModel;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The packet tool pages of the Tools hub: Packet Spammer and Packet Interceptor (ports of the desktop windows).
/// Created once with the hub; their lists and rules are kept in the tools' store (<c>tools.json</c>).
/// </summary>
public sealed class PacketToolPages : IDisposable
{
    private readonly ToolsViewModel _tools;
    private object? _shown;
    private bool _visible;

    private PacketToolPages(ToolsViewModel tools, AppServices services, PluginStore store)
    {
        _tools = tools;
        Spammer = new PacketSpammerViewModel(services, store);
        Interceptor = new PacketInterceptorViewModel(services, store);
        tools.Entries.Add(new ToolEntry("Packet Spammer", "Send a list of game packets in turn at an interval, with the game's pacing as the minimum, a cap per run and a stop on any \"slow down\". Risky: see the warning.",
            () => tools.Show(Spammer, "Packet Spammer")));
        tools.Entries.Add(new ToolEntry("Packet Interceptor", "Rules that change or drop game messages on the connection (off at every start), and the log of what they changed. Never touches the login.",
            () => tools.Show(Interceptor, "Packet Interceptor")));
        tools.PropertyChanged += OnToolsChanged;
    }

    public PacketSpammerViewModel Spammer { get; }

    public PacketInterceptorViewModel Interceptor { get; }

    /// <summary>Adds the pages to <paramref name="tools"/>; <paramref name="store"/> is the tools' store (shared with the game tool pages).</summary>
    public static PacketToolPages AddTo(ToolsViewModel tools, AppServices services, PluginStore store) => new(tools, services, store);

    /// <summary>Whether the Tools hub is on screen (Settings tab, hub open, app in the foreground).</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Update();
        }
    }

    /// <summary>System back: cancels the open page's question, then closes the rule editor. False when neither was open.</summary>
    public bool CancelQuestion() =>
        ConfirmOf(_tools.CurrentPage)?.CancelIfOpen() == true
        || (ReferenceEquals(_tools.CurrentPage, Interceptor) && Interceptor.GoBack());

    private ConfirmHost? ConfirmOf(object? page) => page switch
    {
        PacketSpammerViewModel s => s.Confirm,
        PacketInterceptorViewModel i => i.Confirm,
        _ => null,
    };

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolsViewModel.CurrentPage))
            return;
        object? page = _tools.CurrentPage;
        if (!ReferenceEquals(page, _shown))
        {
            ConfirmOf(_shown)?.CancelIfOpen();
            _shown = page;
        }
        Update();
    }

    private void Update() => Interceptor.Visible = _visible && ReferenceEquals(_tools.CurrentPage, Interceptor);

    internal void Apply(EngineSnapshot s) => Spammer.Apply(s);

    public void Dispose()
    {
        _tools.PropertyChanged -= OnToolsChanged;
        Spammer.Stop();
        Interceptor.Dispose();
    }
}
