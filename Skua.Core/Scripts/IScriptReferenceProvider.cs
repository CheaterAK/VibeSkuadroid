using Microsoft.CodeAnalysis;

namespace Skua.Core.Scripts;

/// <summary>
/// Supplies the base metadata references every script is compiled against.
/// </summary>
/// <remarks>
/// Register an implementation in the host's service collection to replace the default
/// <see cref="DesktopScriptReferenceProvider"/>. Hosts where assemblies have no file location
/// (for example Android, where they live inside the APK) can use <see cref="StreamScriptReferenceProvider"/>.
/// References added per script (plugins, <c>//cs_ref</c>, compiled includes) are added on top of these.
/// </remarks>
public interface IScriptReferenceProvider
{
    /// <summary>
    /// Gets the base references. Implementations should cache the result; this is called for every compiler instance.
    /// </summary>
    IReadOnlyList<PortableExecutableReference> GetReferences();
}
