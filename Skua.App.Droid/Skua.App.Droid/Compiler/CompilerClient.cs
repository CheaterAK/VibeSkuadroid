using Android.Content;
using Android.OS;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.Scripting;

namespace Skua.App.Droid.Compiler;

/// <summary>
/// The engine's link to its compiler process (<see cref="IScriptCompileService"/> on Android): binds the compiler
/// service for one request (which creates the process), sends the request, waits for the answer, unbinds (the process
/// then ends itself). Never on the UI thread: the engine awaits it from its script start; the binding callbacks only
/// hand over a Binder. The compiler's death while compiling (Android ends it when memory runs short) fails the request
/// at once (<see cref="CompileServiceException.Died"/>), it never hangs.
/// </summary>
internal sealed class CompilerClient : IScriptCompileService
{
    private readonly Context _context;
    private readonly int _slot;
    private readonly HandlerThread _thread;

    /// <param name="slot">The engine: 1 for the main process, 2..6 for a full army slot.</param>
    public CompilerClient(Context context, int slot)
    {
        _context = context.ApplicationContext ?? context;
        _slot = slot;
        _thread = new HandlerThread($"compiler-client-{slot}");
        _thread.Start();
    }

    public async Task<CompileResponse> CompileAsync(CompileRequest request, CancellationToken cancellationToken)
    {
        Pending pending = new(request.Id);
        Messenger local = new(new ReplyHandler(_thread.Looper!, pending));
        Connection connection = new(pending, request, local);
        Intent intent = new(_context, CompilerServiceBase.TypeFor(_slot));
        bool bound;
        try
        {
            // AutoCreate starts the process; Important gives it this (foreground-service) process's priority, so
            // Android does not take it for a cached background process while it compiles.
            bound = _context.BindService(intent, connection, Bind.AutoCreate | Bind.Important);
        }
        catch (Java.Lang.Exception e)
        {
            throw new CompileServiceException($"Android refused to start the compiler process ({e.GetType().Name})");
        }
        if (!bound)
            throw new CompileServiceException("Android refused to start the compiler process");
        try
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => pending.Result.TrySetCanceled(cancellationToken));
            return await pending.Result.Task.ConfigureAwait(false);
        }
        finally
        {
            connection.Release();
            try
            {
                _context.UnbindService(connection);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }
        }
    }

    private sealed class Pending
    {
        public Pending(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public TaskCompletionSource<CompileResponse> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        private readonly Pending _pending;
        private readonly CompileRequest _request;
        private readonly Messenger _local;
        private DeathWatch? _death;
        private IBinder? _binder;

        public Connection(Pending pending, CompileRequest request, Messenger local)
        {
            _pending = pending;
            _request = request;
            _local = local;
        }

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is null)
            {
                _pending.Result.TrySetException(new CompileServiceException("the compiler service gave no binder"));
                return;
            }
            try
            {
                _death = new DeathWatch(_pending);
                service.LinkToDeath(_death, 0);
                _binder = service;
                Message m = Message.Obtain(null, CompilerServiceBase.Wire.Request)!;
                Bundle data = new();
                data.PutByteArray(CompilerServiceBase.Wire.Key, ScriptCompileProtocol.Encode(_request));
                m.Data = data;
                m.ReplyTo = _local;
                new Messenger(service).Send(m);
            }
            catch (Exception e) when (e is RemoteException or Java.Lang.Exception)
            {
                _pending.Result.TrySetException(new CompileServiceException("the compiler process ended before it got the request", died: true));
            }
        }

        // The process died (crash, killed for memory): fail now; a rebind would compile again unasked.
        public void OnServiceDisconnected(ComponentName? name) =>
            _pending.Result.TrySetException(new CompileServiceException("the compiler process ended while compiling", died: true));

        public void OnBindingDied(ComponentName? name) =>
            _pending.Result.TrySetException(new CompileServiceException("the compiler process ended while compiling", died: true));

        public void OnNullBinding(ComponentName? name) =>
            _pending.Result.TrySetException(new CompileServiceException("the compiler service gave no binder"));

        public void Release()
        {
            try
            {
                if (_binder is not null && _death is not null)
                    _binder.UnlinkToDeath(_death, 0);
            }
            catch (Java.Lang.Exception)
            {
            }
            _binder = null;
            _death = null;
        }
    }

    private sealed class DeathWatch : Java.Lang.Object, IBinderDeathRecipient
    {
        private readonly Pending _pending;

        public DeathWatch(Pending pending)
        {
            _pending = pending;
        }

        public void BinderDied() =>
            _pending.Result.TrySetException(new CompileServiceException("the compiler process ended while compiling", died: true));
    }

    private sealed class ReplyHandler : Handler
    {
        private readonly Pending _pending;

        public ReplyHandler(Looper looper, Pending pending)
            : base(looper)
        {
            _pending = pending;
        }

        public override void HandleMessage(Message msg)
        {
            try
            {
                // Only this app's own processes answer (non-exported service; checked again here).
                if (msg.What != CompilerServiceBase.Wire.Response || msg.SendingUid != Android.OS.Process.MyUid())
                    return;
                if (ScriptCompileProtocol.DecodeResponse(msg.Data?.GetByteArray(CompilerServiceBase.Wire.Key)) is CompileResponse r && r.Id == _pending.Id)
                    _pending.Result.TrySetResult(r);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("compiler reply", e);
            }
        }
    }
}
