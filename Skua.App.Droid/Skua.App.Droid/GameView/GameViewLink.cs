using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Skua.App.Droid.Core.GameView;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid.GameView;

/// <summary>
/// The messages between the game view (its own ":gameview" process) and the engine process, over a
/// <see cref="Messenger"/> on the non-exported <see cref="GameViewLinkService"/>: only this app (same Linux uid) can
/// bind it, and every message is checked for that uid. The view binds with BIND_AUTO_CREATE, which also lends the
/// engine process the visible view's priority while it is open.
/// </summary>
internal static class GameViewLinkProtocol
{
    /// <summary>The only Intent extra of the game view: the switch's session id (random, not an account secret).</summary>
    public const string ExtraSession = "com.vibeskua.droid.extra.GAMEVIEW_SESSION";

    // View -> engine process (replyTo = the view's Messenger).
    public const int Hello = 1;

    /// <summary>Asks for the stored login (auto-login); answered with <see cref="CredentialsOk"/> or <see cref="NoCredentials"/>.</summary>
    public const int Credentials = 2;
    public const int Stage = 3;
    public const int Closing = 4;

    // Engine process -> view.
    public const int HelloOk = 101;
    public const int Rejected = 102;

    /// <summary>
    /// The stored login, in the reply's Bundle (<see cref="KeyUser"/>, <see cref="KeyPassword"/>): parcelled in memory
    /// from this app's process to this app's process over the non-exported link; never in an Intent, a file or a log.
    /// </summary>
    public const int CredentialsOk = 103;
    public const int NoCredentials = 104;
    public const int Close = 201;

    public const string KeySession = "session";
    public const string KeyOptions = "options";
    public const string KeyAccount = "account";
    public const string KeyStage = "stage";
    public const string KeyReason = "reason";
    public const string KeyClosed = "closed";
    public const string KeySummary = "summary";
    public const string KeyUser = "user";
    public const string KeyPassword = "password";

    public static Message Make(int what, Bundle? data = null)
    {
        Message m = Message.Obtain(null, what)!;
        if (data is not null)
            m.Data = data;
        return m;
    }
}

/// <summary>
/// The engine process's end of the game view link: forwards the view's messages to the <see cref="GameViewSwitch"/>
/// and tells it when the view's process dies (binder death). Not exported.
/// </summary>
[Service(Exported = false)]
[Register("com.vibeskua.droid.GameViewLinkService")]
public sealed class GameViewLinkService : Service
{
    private static readonly object s_lock = new();
    private static (string Session, Messenger View)? s_view;

    private Messenger? _messenger;

    public GameViewLinkService()
    {
    }

    public GameViewLinkService(nint handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    public override IBinder? OnBind(Intent? intent)
    {
        _messenger ??= new Messenger(new LinkHandler());
        return _messenger.Binder;
    }

    /// <summary>Asks the connected view of <paramref name="session"/> to log out and close; false when none is connected.</summary>
    public static bool SendClose(string session, string reason)
    {
        Messenger? view;
        lock (s_lock)
            view = s_view is { } v && v.Session == session ? v.View : null;
        if (view is null)
            return false;
        Bundle b = new();
        b.PutString(GameViewLinkProtocol.KeyReason, reason);
        try
        {
            view.Send(GameViewLinkProtocol.Make(GameViewLinkProtocol.Close, b));
            return true;
        }
        catch (RemoteException)
        {
            return false;
        }
    }

    private sealed class LinkHandler : Handler
    {
        public LinkHandler()
            : base(Looper.MainLooper!)
        {
        }

        public override void HandleMessage(Message msg)
        {
            try
            {
                HandleLink(msg);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("game view link message", e);
            }
        }

        private static void HandleLink(Message msg)
        {
            // Only this app's own processes (same uid) may talk to the link; the service is not exported either.
            if (msg.SendingUid != Process.MyUid())
                return;
            if (!EngineRuntime.IsInitialized || EngineRuntime.GameView is not GameViewSwitch sw)
            {
                Reply(msg.ReplyTo, GameViewLinkProtocol.Rejected, null);
                return;
            }
            string? session = msg.Data?.GetString(GameViewLinkProtocol.KeySession);
            if (string.IsNullOrEmpty(session))
                return;
            switch (msg.What)
            {
                case GameViewLinkProtocol.Hello:
                    OnHello(sw, session, msg.ReplyTo);
                    break;
                case GameViewLinkProtocol.Credentials:
                    OnCredentials(sw, session, msg.ReplyTo);
                    break;
                case GameViewLinkProtocol.Stage:
                    sw.ReportStage(session, msg.Data!.GetString(GameViewLinkProtocol.KeyStage) ?? string.Empty);
                    break;
                case GameViewLinkProtocol.Closing:
                    sw.ViewClosing(session,
                        msg.Data!.GetString(GameViewLinkProtocol.KeyReason) ?? "closed",
                        msg.Data.GetBoolean(GameViewLinkProtocol.KeyClosed, false),
                        msg.Data.GetString(GameViewLinkProtocol.KeySummary));
                    lock (s_lock)
                        if (s_view is { } v && v.Session == session)
                            s_view = null;
                    break;
            }
        }

        private static void OnHello(GameViewSwitch sw, string session, Messenger? view)
        {
            GameViewHello? hello = view is null ? null : sw.Connect(session);
            if (hello is null || view is null)
            {
                Reply(view, GameViewLinkProtocol.Rejected, null);
                return;
            }
            lock (s_lock)
                s_view = (session, view);
            try
            {
                // The view's process dying (crash, killed for memory) breaks this binder: back to headless.
                view.Binder!.LinkToDeath(new ViewDeath(session), 0);
            }
            catch (RemoteException)
            {
                sw.ViewGone(session, "the game view process ended");
                return;
            }
            Bundle b = new();
            b.PutString(GameViewLinkProtocol.KeyOptions, hello.OptionsJson);
            if (hello.AutoLoginAccount is string account)
                b.PutString(GameViewLinkProtocol.KeyAccount, account);
            Reply(view, GameViewLinkProtocol.HelloOk, b);
        }

        private static void OnCredentials(GameViewSwitch sw, string session, Messenger? view)
        {
            // Only the connected view of this session gets them (the switch checks the session and its state too).
            bool connected;
            lock (s_lock)
                connected = view is not null && s_view is { } v && v.Session == session && v.View.Binder?.Equals(view.Binder) == true;
            GameViewCredentials? login = connected ? sw.TakeCredentials(session) : null;
            if (login is null)
            {
                Reply(view, GameViewLinkProtocol.NoCredentials, null);
                return;
            }
            Bundle b = new();
            b.PutString(GameViewLinkProtocol.KeyUser, login.Account);
            b.PutString(GameViewLinkProtocol.KeyPassword, login.Password);
            Reply(view, GameViewLinkProtocol.CredentialsOk, b);
            b.Clear();
        }

        private static void Reply(Messenger? to, int what, Bundle? data)
        {
            if (to is null)
                return;
            try
            {
                to.Send(GameViewLinkProtocol.Make(what, data));
            }
            catch (RemoteException)
            {
            }
        }
    }

    private sealed class ViewDeath : Java.Lang.Object, IBinderDeathRecipient
    {
        private readonly string _session;

        public ViewDeath(string session)
        {
            _session = session;
        }

        public void BinderDied()
        {
            lock (s_lock)
                if (s_view is { } v && v.Session == _session)
                    s_view = null;
            new Handler(Looper.MainLooper!).Post(() =>
            {
                if (EngineRuntime.IsInitialized && EngineRuntime.GameView is GameViewSwitch sw)
                    sw.ViewGone(_session, "the game view process ended");
            });
        }
    }
}

/// <summary>Starts the game view activity (":gameview" process) with nothing but the switch's session id.</summary>
internal sealed class AndroidGameViewLauncher : IGameViewLauncher
{
    private readonly Context _context;

    public AndroidGameViewLauncher(Context context)
    {
        _context = context;
    }

    public bool Launch(string session)
    {
        Intent intent = new Intent(_context, typeof(GameViewActivity)).PutExtra(GameViewLinkProtocol.ExtraSession, session)!;
        Activity? activity = AndroidPlatformServices.CurrentActivity;
        try
        {
            if (activity is not null && !activity.IsFinishing)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(intent);
            }
            return true;
        }
        catch (Java.Lang.Exception e)
        {
            CrashCapture.NonFatal("starting the game view", e);
            return false;
        }
    }

    public void RequestClose(string session, string reason) => GameViewLinkService.SendClose(session, reason);
}
