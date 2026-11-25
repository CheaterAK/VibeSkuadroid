using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.Scripting;
using Environment = System.Environment;

namespace Skua.App.Droid.Compiler;

/// <summary>
/// The script compiler, in its own short-lived process: <c>:compiler</c> for the main engine, <c>:compilerN</c> for
/// army slot N (declared in AndroidManifest.xml; not exported, protected by the signature-level permission
/// <c>...permission.SLOT_IPC</c>). Bound (never started) by its engine for one compile: the request comes in a
/// <see cref="Messenger"/> message (<see cref="ScriptCompileProtocol"/> JSON), <see cref="ScriptCompileWorker"/>
/// compiles into the engine's compiled-script cache, the answer goes to <c>replyTo</c>, and once nobody is bound and
/// nothing compiles the process ends itself, so Roslyn's memory goes back to the system at once.
/// </summary>
/// <remarks>
/// The process takes its engine's HOME from its own name (<see cref="CompilerProcess.PreparePaths"/>, before anything
/// reads the environment), so it compiles into exactly the folders and cache keys its engine uses; a request only names
/// a script, which must be inside that scripts folder. Every message is checked for <c>sendingUid == myUid</c>.
/// </remarks>
public abstract class CompilerServiceBase : Service
{
    private const string PayloadKey = "p";
    private const int WhatRequest = 1;
    private const int WhatResponse = 2;

    /// <summary>How long the process lingers once unbound and idle before it ends itself.</summary>
    private static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Messenger? _messenger;
    private HandlerThread? _thread;
    private Handler? _handler;
    private int _busy;
    private bool _bound;

    protected CompilerServiceBase()
    {
    }

    protected CompilerServiceBase(nint handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    /// <summary>The compiler service class for an engine: slot 1 (the main process) or 2..6.</summary>
    public static Type TypeFor(int slot) => slot switch
    {
        1 => typeof(CompilerService),
        2 => typeof(CompilerService2),
        3 => typeof(CompilerService3),
        4 => typeof(CompilerService4),
        5 => typeof(CompilerService5),
        6 => typeof(CompilerService6),
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public override void OnCreate()
    {
        base.OnCreate();
        _thread = new HandlerThread("compiler-ipc");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);
        _messenger = new Messenger(new IncomingHandler(_thread.Looper!, OnMessage));
        CrashCapture.Info($"compiler process {CompilerProcess.Slot} started");
    }

    public override IBinder? OnBind(Intent? intent)
    {
        lock (_gate)
            _bound = true;
        return _messenger?.Binder;
    }

    public override bool OnUnbind(Intent? intent)
    {
        lock (_gate)
            _bound = false;
        ScheduleExit();
        return false;
    }

    // On the IPC thread; the Message is recycled when this returns.
    private void OnMessage(Message msg)
    {
        if (msg.What != WhatRequest || msg.SendingUid != Android.OS.Process.MyUid())
            return;
        CompileRequest? request = ScriptCompileProtocol.DecodeRequest(msg.Data?.GetByteArray(PayloadKey));
        Messenger? reply = msg.ReplyTo;
        if (request is null || reply is null)
            return;
        Interlocked.Increment(ref _busy);
        _ = Task.Run(() =>
        {
            CompileResponse response;
            try
            {
                response = CompilerProcess.Worker(this) is ScriptCompileWorker worker
                    ? worker.Compile(request)
                    : new CompileResponse(request.Id, CompileOutcome.Failed, "the compiler has no script references", null, 0, -1, 0);
            }
            catch (Exception e)
            {
                response = new CompileResponse(request.Id, CompileOutcome.Failed, $"{e.GetType().Name}: {e.Message}", null, 0, -1, 0);
            }
            CrashCapture.Info($"compiler {CompilerProcess.Slot}: {Path.GetFileName(request.ScriptPath)} {response.Outcome}, {response.Compilations} compilations, {response.Seconds:0.0} s, peak {response.PeakRssBytes >> 20} MB");
            Send(reply, ScriptCompileProtocol.Encode(response));
            Interlocked.Decrement(ref _busy);
            ScheduleExit();
        });
    }

    private static void Send(Messenger client, byte[] bytes)
    {
        try
        {
            Message m = Message.Obtain(null, WhatResponse)!;
            Bundle data = new();
            data.PutByteArray(PayloadKey, bytes);
            m.Data = data;
            client.Send(m);
        }
        catch (Exception e) when (e is RemoteException or DeadObjectException or Java.Lang.Exception)
        {
            // The engine is gone (its process died): nothing to answer.
        }
    }

    // Ends the process once nothing compiles and nobody is bound: all its memory returns to the system.
    private void ScheduleExit()
    {
        _handler?.PostDelayed(() =>
        {
            lock (_gate)
            {
                if (_bound || Volatile.Read(ref _busy) > 0)
                    return;
            }
            CrashCapture.Info($"compiler process {CompilerProcess.Slot} ends (idle)");
            StopSelf();
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }, (long)IdleExit.TotalMilliseconds);
    }

    public override void OnDestroy()
    {
        _thread?.QuitSafely();
        base.OnDestroy();
    }

    private sealed class IncomingHandler : Handler
    {
        private readonly Action<Message> _on;

        public IncomingHandler(Looper looper, Action<Message> on)
            : base(looper)
        {
            _on = on;
        }

        public override void HandleMessage(Message msg)
        {
            try
            {
                _on(msg);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("compiler IPC message", e);
            }
        }
    }

    /// <summary>Wire format shared with <see cref="CompilerClient"/>.</summary>
    internal static class Wire
    {
        public const string Key = PayloadKey;
        public const int Request = WhatRequest;
        public const int Response = WhatResponse;
    }
}

/// <summary>The compiler process's composition: its slot (from its name), HOME, and the worker.</summary>
internal static class CompilerProcess
{
    public const string ProcessPrefix = ":compiler";

    private static readonly object s_gate = new();
    private static ScriptCompileWorker? s_worker;

    /// <summary>The engine this compiler serves: 1 (":compiler", the main process) or 2..6 (":compilerN"), 0 when not a compiler process.</summary>
    public static int Slot { get; private set; }

    /// <summary>The engine slot of this process from its name, or null when it is not a compiler process.</summary>
    public static int? Detect(string? processName)
    {
        if (processName is null)
            return null;
        int at = processName.LastIndexOf(ProcessPrefix, StringComparison.Ordinal);
        if (at < 0)
            return null;
        string rest = processName[(at + ProcessPrefix.Length)..];
        if (rest.Length == 0)
            return 1;
        return int.TryParse(rest, out int n) && SlotIds.IsRemote(n) && rest == n.ToString(System.Globalization.CultureInfo.InvariantCulture) ? n : null;
    }

    /// <summary>
    /// HOME and TMPDIR of the engine this compiler serves, on the main thread before anything reads the environment
    /// (Skua.Core captures <c>$HOME/.config</c> once): the main process's folders, or slot N's (<see cref="SlotLayout"/>).
    /// </summary>
    public static void PreparePaths(Context context, int slot)
    {
        Context application = context.ApplicationContext ?? context;
        AppPaths main = new(application.FilesDir!.AbsolutePath, application.NoBackupFilesDir!.AbsolutePath, application.CacheDir!.AbsolutePath);
        AppPaths paths = slot == 1 ? main : SlotLayout.For(main, slot).Paths;
        Environment.SetEnvironmentVariable("HOME", paths.FilesDir);
        Environment.SetEnvironmentVariable("TMPDIR", paths.CacheDir);
        Slot = slot;
    }

    /// <summary>The worker (created on the first request): the APK's references, one compile at a time app-wide.</summary>
    public static ScriptCompileWorker? Worker(Context context)
    {
        lock (s_gate)
        {
            if (s_worker is not null)
                return s_worker;
            Context application = context.ApplicationContext ?? context;
            ScriptReferenceSet references = EngineRuntime.LoadScriptReferences(application);
            if (!references.IsComplete)
                return null;
            // In the main files folder, shared by every compiler process: army slots compiling at once wait in turn.
            string lockFile = Path.Combine(application.FilesDir!.AbsolutePath, "compile.lock");
            s_worker = new ScriptCompileWorker(references, lockFile);
            return s_worker;
        }
    }
}

// One class per compiler process: a component runs in the one process the manifest names for it.
[Register("com.vibeskua.droid.CompilerService")]
public sealed class CompilerService : CompilerServiceBase
{
    public CompilerService() { }

    public CompilerService(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.CompilerService2")]
public sealed class CompilerService2 : CompilerServiceBase
{
    public CompilerService2() { }

    public CompilerService2(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.CompilerService3")]
public sealed class CompilerService3 : CompilerServiceBase
{
    public CompilerService3() { }

    public CompilerService3(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.CompilerService4")]
public sealed class CompilerService4 : CompilerServiceBase
{
    public CompilerService4() { }

    public CompilerService4(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.CompilerService5")]
public sealed class CompilerService5 : CompilerServiceBase
{
    public CompilerService5() { }

    public CompilerService5(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.CompilerService6")]
public sealed class CompilerService6 : CompilerServiceBase
{
    public CompilerService6() { }

    public CompilerService6(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}
