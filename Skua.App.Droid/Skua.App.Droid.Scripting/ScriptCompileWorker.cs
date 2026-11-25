using Skua.Core;
using Skua.Core.Headless.Host;
using Skua.Core.Models;
using Skua.Core.Scripts;
using System.Diagnostics;
using System.Globalization;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// The compiler process's side: compiles one script and its includes with the engine's own pipeline (a Skua.Core
/// session with the same references, namespaces, reported version and load context, Skua's ScriptManager and
/// Compiler) into the compiled-script cache the engine reads, under the same keys, without creating the script object
/// (<see cref="ScriptManager.CompileToCache"/>). Platform neutral: the Android <c>:compiler</c> service and the Linux test
/// host both drive it.
/// </summary>
/// <remarks>
/// The process must have the requesting engine's HOME (Skua.Core derives the scripts folder, the cache and the cache keys
/// from it): on Android the compiler process of slot N is <c>:compilerN</c> and sets slot N's HOME, so a path in a
/// request never selects folders. One compile at a time across every compiler process of the app (<see cref="LockFile"/>),
/// so army slots compiling at once do not add their peaks up.
/// </remarks>
public sealed class ScriptCompileWorker : IAsyncDisposable
{
    private readonly ScriptReferenceSet _references;
    private readonly Action<HeadlessSkuaOptions>? _configure;
    private readonly object _gate = new();
    private HeadlessSkuaSession? _session;

    /// <param name="references">The reference images (the APK's, like the engine's).</param>
    /// <param name="lockFile">A file every compiler process of the app locks while compiling (null: no cross-process lock).</param>
    /// <param name="configure">Adjusts the session options (tests: the data folder).</param>
    public ScriptCompileWorker(ScriptReferenceSet references, string? lockFile, Action<HeadlessSkuaOptions>? configure = null)
    {
        _references = references;
        LockFile = lockFile;
        _configure = configure;
    }

    public string? LockFile { get; }

    /// <summary>How long to wait for another compiler process's compile before giving up.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Compiles (blocking; call on a worker thread). Never throws: every failure is an outcome.</summary>
    public CompileResponse Compile(CompileRequest request)
    {
        Stopwatch sw = Stopwatch.StartNew();
        long before = Compiler.Compilations;
        CompileResponse Answer(CompileOutcome outcome, string? message, string? code = null) =>
            new(request.Id, outcome, message, code, sw.Elapsed.TotalSeconds, ReadPeakRss(), Compiler.Compilations - before);

        if (request.V != ScriptCompileProtocol.Version)
            return Answer(CompileOutcome.Refused, $"protocol version {request.V}, expected {ScriptCompileProtocol.Version}");
        if (!string.Equals(request.ReferenceSet, _references.Identity, StringComparison.Ordinal))
            return Answer(CompileOutcome.Refused, "the engine's script references differ from the compiler's (the app was updated?)");
        HeadlessSkuaSession session;
        try
        {
            lock (_gate)
                session = Session(); // first: it pins Skua's data folder, which the scripts folder below comes from
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Answer(CompileOutcome.Failed, $"the compiler's Skua session did not start: {e.GetType().Name}: {e.Message}");
        }
        string scripts = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path;
        try
        {
            path = Path.GetFullPath(request.ScriptPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Answer(CompileOutcome.Refused, "not a valid path");
        }
        if (!path.StartsWith(scripts, StringComparison.Ordinal) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Answer(CompileOutcome.Refused, "the script is not a .cs file inside the scripts folder");
        if (!File.Exists(path))
            return Answer(CompileOutcome.Refused, "the script file does not exist");

        lock (_gate)
        {
            using FileStream? held = AcquireLock();
            try
            {
                ScriptManager manager = (ScriptManager)session.Scripts;
                manager.SetLoadedScript(path);
                manager.CompileToCache(File.ReadAllText(path));
                return Answer(CompileOutcome.Compiled, null);
            }
            catch (ScriptCompileException e)
            {
                return Answer(CompileOutcome.CompileError, e.Message, e.CompiledScript);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Answer(CompileOutcome.Failed, $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private HeadlessSkuaSession Session()
    {
        if (_session is not null)
            return _session;
        // The options of SkuaScriptHostAdapter.OpenSession that shape a compile. Not its ConfigureSettings: Skua saves
        // every Set to the settings file the engine process shares (same HOME), and the engine already saved those.
        HeadlessSkuaOptions options = new()
        {
            ScriptReferences = _references.Provider,
            ScriptNamespaces = ScriptReferenceSet.Namespaces,
            ApplicationVersionOverride = SkuaScriptHostAdapter.ReportedSkuaVersion,
        };
        _configure?.Invoke(options);
        HeadlessSkuaSession session = HeadlessSkuaSession.Create(options);
        ((ScriptManager)session.Scripts).LoadContextFactory = new StoreScriptLoadContext(new ScriptAssemblyStore()).BeginCompile;
        _session = session;
        return session;
    }

    // One compile at a time across the app's compiler processes: an exclusive lock on a shared file (flock on Linux).
    private FileStream? AcquireLock()
    {
        if (LockFile is null)
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(LockFile)!);
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < LockTimeout)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Peak resident memory of this process (VmHWM), -1 where /proc is missing.</summary>
    public static long ReadPeakRss()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb * 1024 : -1;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        HeadlessSkuaSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}
