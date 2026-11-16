namespace Skua.Core.Scripts;

/// <summary>
/// An <see cref="IScriptNamespaceProvider"/> backed by a fixed list of namespaces.
/// </summary>
public sealed class ScriptNamespaceProvider : IScriptNamespaceProvider
{
    /// <summary>
    /// The namespaces the desktop client has always imported into scripts.
    /// </summary>
    /// <remarks>
    /// <c>System.Windows.Forms</c> only resolves when the references contain an assembly defining that namespace:
    /// the WindowsDesktop runtime on Windows, or a compatibility shim on other hosts.
    /// </remarks>
    public static IReadOnlyList<string> DesktopNamespaces { get; } = new[]
    {
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.Drawing",
        "System.Dynamic",
        "System.Globalization",
        "System.IO",
        "System.Linq",
        "System.Net",
        "System.Net.Http",
        "System.Reflection",
        "System.Runtime.CompilerServices",
        "System.Text",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Timers",
        "System.Windows.Forms",
        "Microsoft.Win32",
        "Skua.Core",
        "Skua.Core.Interfaces",
        "Skua.Core.Models",
        "Skua.Core.Models.Items",
        "Skua.Core.Models.Monsters",
        "Skua.Core.Models.Players",
        "Skua.Core.Models.Quests",
        "Skua.Core.Models.Servers",
        "Skua.Core.Models.Shops",
        "Skua.Core.Models.Skills",
        "Skua.Core.Models.Auras",
        "Skua.Core.Models.Factions",
        "Skua.Core.Options",
        "Skua.Core.Utils",
        "CommunityToolkit.Mvvm.DependencyInjection",
        "Newtonsoft.Json",
        "Newtonsoft.Json.Linq",
    };

    /// <summary>
    /// The provider used when the host registers none: <see cref="DesktopNamespaces"/>.
    /// </summary>
    public static ScriptNamespaceProvider Desktop { get; } = new(DesktopNamespaces);

    private readonly string[] _namespaces;

    public ScriptNamespaceProvider(IEnumerable<string> namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        _namespaces = namespaces.ToArray();
    }

    public IReadOnlyList<string> GetNamespaces() => _namespaces;
}
