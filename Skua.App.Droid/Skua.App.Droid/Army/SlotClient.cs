using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid.Army;

/// <summary>
/// The coordinator's link to one slot process (<see cref="ISlotChannel"/> on Android): starts the slot's foreground
/// service with an explicit intent (an action only, nothing else), binds it, and exchanges Messenger messages with it on
/// a dedicated looper thread. Android rebinds by itself when the slot process restarts; every rebind raises
/// <see cref="ConnectionChanged"/> and the remote engine says hello again.
/// </summary>
internal sealed class SlotClient : ISlotChannel
{
    private readonly Context _context;
    private readonly int _slot;
    private readonly HandlerThread _thread;
    private readonly Messenger _local;
    private readonly Connection _connection;
    private volatile Messenger? _remote;
    private bool _bound;

    public SlotClient(Context context, int slot)
    {
        _context = context.ApplicationContext ?? context;
        _slot = slot;
        _thread = new HandlerThread($"slot-client-{slot}");
        _thread.Start();
        _local = new Messenger(new ReplyHandler(_thread.Looper!, this));
        _connection = new Connection(this);
    }

    public bool IsConnected => _remote is not null;

    public event Action<byte[]>? Received;

    public event Action<bool>? ConnectionChanged;

    /// <summary>Starts the slot (foreground service, so it outlives the UI) and binds it. False when Android refused the start.</summary>
    public bool Start()
    {
        Intent intent = new Intent(_context, SlotEngineService.TypeFor(_slot)).SetAction(SlotEngineService.ActionStart);
        try
        {
            ContextCompat.StartForegroundService(_context, intent);
        }
        catch (Java.Lang.Exception e)
        {
            CrashCapture.Info($"slot {_slot} could not start: {e.GetType().Name}");
            return false;
        }
        if (!_bound)
            _bound = _context.BindService(new Intent(_context, SlotEngineService.TypeFor(_slot)), _connection, Bind.AutoCreate);
        return _bound;
    }

    /// <summary>Binds without starting: connects only if the slot's service runs (or when it is started later).</summary>
    public bool Attach()
    {
        if (!_bound)
            _bound = _context.BindService(new Intent(_context, SlotEngineService.TypeFor(_slot)), _connection, (Bind)0);
        return _bound;
    }

    /// <summary>Logs the slot out, stops its service and ends its process (memory comes back at once).</summary>
    public void Shutdown()
    {
        Unbind();
        try
        {
            ContextCompat.StartForegroundService(_context, new Intent(_context, SlotEngineService.TypeFor(_slot)).SetAction(SlotEngineService.ActionShutdown));
        }
        catch (Java.Lang.Exception e)
        {
            CrashCapture.Info($"slot {_slot} shutdown: {e.GetType().Name}");
        }
    }

    public bool Send(byte[] message)
    {
        Messenger? remote = _remote;
        if (remote is null)
            return false;
        try
        {
            Message m = Message.Obtain(null, SlotEngineService.Wire.Request)!;
            Bundle data = new();
            data.PutByteArray(SlotEngineService.Wire.Key, message);
            m.Data = data;
            m.ReplyTo = _local;
            remote.Send(m);
            return true;
        }
        catch (Exception e) when (e is RemoteException or Java.Lang.Exception)
        {
            SetRemote(null);
            return false;
        }
    }

    private void SetRemote(Messenger? remote)
    {
        bool was = _remote is not null;
        _remote = remote;
        if (was != (remote is not null))
            ConnectionChanged?.Invoke(remote is not null);
    }

    private void OnReply(Message msg)
    {
        // Only this app's own processes answer (non-exported service; checked again here).
        if (msg.SendingUid != Android.OS.Process.MyUid())
            return;
        byte[]? bytes = msg.Data?.GetByteArray(SlotEngineService.Wire.Key);
        if (bytes is not null)
            Received?.Invoke(bytes);
    }

    private void Unbind()
    {
        if (!_bound)
            return;
        _bound = false;
        try
        {
            _context.UnbindService(_connection);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }
        SetRemote(null);
    }

    public ValueTask DisposeAsync()
    {
        Unbind();
        _thread.QuitSafely();
        return ValueTask.CompletedTask;
    }

    private sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        private readonly SlotClient _owner;

        public Connection(SlotClient owner)
        {
            _owner = owner;
        }

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is not null)
                _owner.SetRemote(new Messenger(service));
        }

        public void OnServiceDisconnected(ComponentName? name) => _owner.SetRemote(null);
    }

    private sealed class ReplyHandler : Handler
    {
        private readonly SlotClient _owner;

        public ReplyHandler(Looper looper, SlotClient owner)
            : base(looper)
        {
            _owner = owner;
        }

        public override void HandleMessage(Message msg)
        {
            try
            {
                _owner.OnReply(msg);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("slot reply", e);
            }
        }
    }
}
