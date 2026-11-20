using System.ComponentModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The Diagnostics page of the Tools hub (desktop: Diagnostics, Runtime helpers, logs). Created once with the hub;
/// the page refreshes only while it is open, the hub is on screen and the app is in the foreground.
/// </summary>
public sealed class DiagnosticsToolPages : IDisposable
{
    private readonly ToolsViewModel _tools;
    private bool _visible;

    private DiagnosticsToolPages(ToolsViewModel tools, AppServices services)
    {
        _tools = tools;
        Diagnostics = new DiagnosticsViewModel(services);
        tools.Entries.Add(new ToolEntry("Diagnostics", "Engine, connection (latency, packets per minute), memory and the watchdog, Flash gaps, process exits, runtime helpers; share a scrubbed bundle.",
            () => tools.Show(Diagnostics, "Diagnostics")));
        tools.PropertyChanged += OnToolsChanged;
    }

    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Adds the page to <paramref name="tools"/>.</summary>
    public static DiagnosticsToolPages AddTo(ToolsViewModel tools, AppServices services) => new(tools, services);

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

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolsViewModel.CurrentPage))
            Update();
    }

    private void Update() => Diagnostics.Visible = _visible && ReferenceEquals(_tools.CurrentPage, Diagnostics);

    public void Dispose()
    {
        _tools.PropertyChanged -= OnToolsChanged;
        Diagnostics.Dispose();
    }
}
