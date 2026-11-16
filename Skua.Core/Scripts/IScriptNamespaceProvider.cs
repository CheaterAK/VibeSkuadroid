namespace Skua.Core.Scripts;

/// <summary>
/// Supplies the <c>using</c> directives prepended to every compiled script.
/// </summary>
/// <remarks>
/// Register an implementation in the host's service collection to replace the default
/// <see cref="ScriptNamespaceProvider.Desktop"/> list.
/// </remarks>
public interface IScriptNamespaceProvider
{
    /// <summary>
    /// Gets the namespaces to import into every script.
    /// </summary>
    IReadOnlyList<string> GetNamespaces();
}
