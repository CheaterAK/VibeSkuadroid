using Skua.Core.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Interfaces;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Xml.Linq;

namespace Skua.Core.Headless.Flash;

/// <summary>How faithfully the bridge answers a callback, game function or path.</summary>
public enum BridgeSupport
{
    /// <summary>Answered from the model and the client rules with skua.swf's return shape.</summary>
    Implemented,

    /// <summary>Display-only: accepted and stored (reads back what was written), nothing to draw.</summary>
    NoOp,

    /// <summary>Cannot exist without Flash; refused and logged.</summary>
    Impossible,

    /// <summary>Known but not implemented: answered undefined and logged as a gap.</summary>
    Missing,
}

/// <summary>
/// <see cref="IFlashUtil"/> without Flash: skua.swf's ExternalInterface callbacks (Skua.AS3 Externalizer.as), the
/// generic object-path reads/writes and game-function calls into the game's AS3 objects, and the events skua.swf
/// raises (<c>requestLoadGame</c>, <c>pre-load</c>, <c>loaded</c>, <c>packetFromServer</c>, <c>pext</c>,
/// <c>packet</c>, <c>connectionLost</c>), all answered from a <see cref="HeadlessGame"/>.
/// </summary>
/// <remarks>
/// <para>Return values follow the Windows FlashUtil: each callback yields what skua.swf returns (JSON text for objects,
/// "true"/"false" strings, numbers, nothing for void), converted to the caller's type with the same rules
/// (<c>ConvertParsedValue</c>, <c>DefaultProvider</c>).</para>
/// <para>Anything not implemented, and anything that cannot exist headless (<c>injectScript</c>, reflection outside the
/// model), is recorded in <see cref="Gaps"/> and answered with the bridge's "no value". Nothing is answered with a
/// made-up default.</para>
/// </remarks>
public sealed partial class HeadlessFlashUtil : IFlashUtil
{
    private readonly Lazy<IScriptManager>? _manager;
    private readonly Dictionary<string, (BridgeSupport Support, Func<object?[], object?> Handler)> _callbacks;
    private readonly Dictionary<string, (BridgeSupport Support, Func<object?[], object?> Handler)> _functions;
    private bool _loadRequested;
    private bool _disposed;

    /// <param name="game">The headless client to answer from.</param>
    /// <param name="manager">Skua's script manager, so a stopping script is cancelled inside a Flash call as on Windows.</param>
    public HeadlessFlashUtil(HeadlessGame game, Lazy<IScriptManager>? manager = null)
    {
        Game = game;
        _manager = manager;
        _callbacks = BuildCallbacks();
        _functions = BuildFunctions();
        _clientTree = BuildClientTree();
        Game.PacketProcessed += OnPacketProcessed;
        Game.PacketWritten += OnPacketWritten;
        Game.ConnectionLost += OnConnectionLost;
        Gaps.NewGap += g => Game_Log($"Flash gap ({g.Kind}): {g.Name}{g.ArgsShape} - {g.Detail}");
    }

    public event FlashCallHandler? FlashCall;

    /// <summary>Lines worth logging (first sight of a gap, handler errors).</summary>
    public event Action<string>? Log;

    public HeadlessGame Game { get; }

    /// <summary>Every call without a faithful answer.</summary>
    public FlashGapLog Gaps { get; } = new();

    /// <summary>The callbacks the bridge answers and how (skua.swf Externalizer names).</summary>
    public IReadOnlyDictionary<string, BridgeSupport> Callbacks => _callbacks.ToDictionary(p => p.Key, p => p.Value.Support);

    /// <summary>The game functions (<c>callGameFunction</c> targets) the bridge answers and how.</summary>
    public IReadOnlyDictionary<string, BridgeSupport> GameFunctions => _functions.ToDictionary(p => p.Key, p => p.Value.Support);

    private void Game_Log(string message)
    {
        Log?.Invoke(message);
    }

    // ---------------------------------------------------------------- lifecycle and events

    /// <summary>skua.swf's Externalizer.init: asks the host to load the game (<c>requestLoadGame</c>).</summary>
    public void InitializeFlash()
    {
        Raise("requestLoadGame");
    }

    // Main.loadGame: "pre-load", then the game file "loads" (there is none) and skua.swf calls "loaded".
    private object? LoadClient()
    {
        if (_loadRequested)
            return null;
        _loadRequested = true;
        Raise("pre-load");
        Raise("loaded");
        return null;
    }

    private void OnPacketProcessed(object? sender, InboundPacket packet)
    {
        // Main.as packetReceived ("packetFromServer") and onExtensionResponse ("pext"), in that order.
        if (FlashCall is null)
            return;
        Raise("packetFromServer", packet.Text);
        if (packet.ToExtensionResponseJson() is string json)
            Raise("pext", json);
    }

    private void OnPacketWritten(object? sender, PacketSentEventArgs e)
    {
        // Main.as packetReceived: "[Sending - STR]: %xt%zm%..." becomes "packet". SmartFox XML (the login) is never reported.
        if (e.Text.StartsWith("%xt%zm%", StringComparison.Ordinal))
            Raise("packet", e.Text.Trim());
    }

    private void OnConnectionLost(object? sender, DisconnectInfo info)
    {
        Raise("connectionLost");
    }

    private void Raise(string function, params object[] args)
    {
        FlashCallHandler? handler = FlashCall;
        if (handler is null)
            return;
        foreach (FlashCallHandler h in handler.GetInvocationList().Cast<FlashCallHandler>())
        {
            try
            {
                h(function, args);
            }
            catch (Exception e)
            {
                // A subscriber that throws (ScriptInterface's pext handler on an unexpected shape) must not stop the reader loop.
                Trace.WriteLine($"Flash call handler for '{function}' failed: {e.Message}");
                Log?.Invoke($"Flash call handler for '{function}' failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- calls

    public string? Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T? Call<T>(string function, params object[] args)
    {
        object? o = Call(function, typeof(T), args);
        if (o is T value)
            return value;
        object? fallback = Skua.Core.Utils.DefaultProvider.GetDefault<T>(typeof(T));
        return fallback is T typed ? typed : default;
    }

    public object? Call(string function, Type type, params object[] args)
    {
        // As on Windows: a script that is being stopped is cancelled at its next Flash call. Unlike Windows, only calls
        // made from script code are cancelled: ScriptManager's own clean-up on the script thread (Skills.Stop, Drops.Stop
        // after the script returned) also calls Flash, and a throw there is unhandled and ends the process.
        if (CancelsScriptCall())
            _manager!.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        object?[] a = args ?? Array.Empty<object>();
        object? raw;
        try
        {
            raw = Invoke(function, a);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The Windows bridge turns an AS3 error into the default value (and a FlashErrorMessage).
            Log?.Invoke($"Flash call {function} failed: {e.GetType().Name}: {e.Message}");
            raw = null;
        }
        return As3.Convert(raw, type);
    }

    // A call from script code on the script thread while the script is being stopped.
    private bool CancelsScriptCall() =>
        _manager is not null && Thread.CurrentThread.Name == "Script Thread" && _manager.Value.ShouldExit && CalledFromScript();

    // A frame of a compiled script (scripts load into their own collectible ScriptLoadContext) is on the stack.
    private static bool CalledFromScript()
    {
        StackFrame[] frames = new StackTrace(2, false).GetFrames();
        foreach (StackFrame f in frames)
        {
            System.Reflection.Assembly? asm = f.GetMethod()?.DeclaringType?.Assembly;
            if (asm is not null && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm) is Skua.Core.ScriptLoadContext)
                return true;
        }
        return false;
    }

    /// <summary>One callback, returning the AS3 value skua.swf would.</summary>
    private object? Invoke(string function, object?[] args)
    {
        if (_callbacks.TryGetValue(function, out (BridgeSupport Support, Func<object?[], object?> Handler) cb))
            return cb.Handler(args);
        Gaps.Record(FlashGapKind.Missing, function, args, "not a skua.swf callback the bridge knows; answered undefined");
        return null;
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "number":
                return int.TryParse(el.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int i)
                    ? i
                    : float.TryParse(el.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float f) ? f : 0;
            case "true":
                return true;
            case "false":
                return false;
            case "null":
            case "undefined":
                return null!;
            case "array":
                return el.Elements().Select(FromFlashXml).ToArray();
            case "object":
                IDictionary<string, object?> d = new ExpandoObject();
                foreach (XElement e in el.Elements())
                    d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First());
                return d;
            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Game.PacketProcessed -= OnPacketProcessed;
        Game.PacketWritten -= OnPacketWritten;
        Game.ConnectionLost -= OnConnectionLost;
    }
}
