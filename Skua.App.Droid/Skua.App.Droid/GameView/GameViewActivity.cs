using System.Globalization;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Skua.App.Droid.Core.GameView;
using Debug = Android.OS.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;
using Uri = Android.Net.Uri;

namespace Skua.App.Droid.GameView;

/// <summary>
/// The game view: a full-screen WebView showing https://game.aq.com/__vibeskua__/index.html, answered by the app
/// (<see cref="RequestPolicy"/>), with Ruffle running this repo's skua.swf, which loads the real game. Ported from the
/// game view spike (Skua.Droid.RuffleSpike).
/// </summary>
/// <remarks>
/// <para>
/// Runs in its own ":gameview" process: Ruffle's memory (the WebView's GPU side lives in the app process; on the
/// measured phone it reached 2.7 GB with the wrong renderer) and a crash of it cannot take the engine down, and the
/// engine process notices the death through the link's binder and takes the account back. Nothing of the engine,
/// Avalonia or the crash screen is started in this process (<see cref="DroidApplication"/>).
/// </para>
/// <para>
/// Started only by <see cref="AndroidGameViewLauncher"/> (not exported) with the switch's session id; it binds
/// <see cref="GameViewLinkService"/> in the engine process, says hello with that id and gets its settings. A stale
/// instance (Android restoring it from Recents after its process died) is rejected and closes. Closing (X, Back, a
/// request from the app, the renderer dying, two minutes in the background): the game logs out, the game sockets
/// close (bounded wait), the WebView is destroyed, its storage and cookies cleared, the engine process is told, and
/// this process ends.
/// </para>
/// </remarks>
[Activity(
    Label = "Game view",
    Exported = false,
    Process = ProcessInfo.GameViewProcessSuffix,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    HardwareAccelerated = true,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    WindowSoftInputMode = SoftInput.AdjustPan | SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
[Register("com.vibeskua.droid.GameViewActivity")]
public sealed class GameViewActivity : Activity
{
    /// <summary>Closed (back to headless) after this long in the background: the bot should not stay logged out unseen.</summary>
    private static readonly TimeSpan BackgroundLimit = TimeSpan.FromMinutes(2);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HttpClient _http = GameApi.CreateClient(); // never follows redirects
    private GameViewOptions _options = new();
    private GameViewRecorder _rec = null!;
    private string _session = string.Empty;
    private FrameLayout _root = null!;
    private TextView _hud = null!, _status = null!;
    private Button _loginButton = null!;
    private WebView? _web;
    private string _configJson = "{}";
    private ServerAllowlist _allowlist = new([]);
    private ChannelBridge? _channel;
    private WebMessagePort? _appPort;
    private Handler _main = null!;
    private HandlerThread? _bridgeThread;
    private Handler? _bridgeHandler;
    private System.Threading.Timer? _memTimer;
    private GameViewMemoryGuard _guard = new(GameViewOptions.DefaultMemoryLimitMb);
    private long _guardDeadline;
    private int _memTicks;
    private Link? _link;
    private Messenger? _engine;
    private string? _autoLoginAccount;
    private bool _prepared, _closing, _shutDown;
    private Action? _backgroundClose;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _main = new Handler(Looper.MainLooper!);
        _session = Intent?.GetStringExtra(GameViewLinkProtocol.ExtraSession) ?? string.Empty;
        _rec = new GameViewRecorder(() => _clock.Elapsed.TotalSeconds);
        Header();
        ClearWebData();
        BuildUi();
        GoFullScreen();
        if (_session.Length == 0)
        {
            Finish();
            return;
        }
        _link = new Link(this);
        if (!BindService(new Intent(this, typeof(GameViewLinkService)), _link, Bind.AutoCreate | Bind.Important))
            ShowStatusAndClose("The app's engine could not be reached.");
    }

    private void Header()
    {
        ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
        ActivityManager.MemoryInfo mi = new();
        am.GetMemoryInfo(mi);
        _rec.Header("time", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        _rec.Header("device", $"{Build.Manufacturer} {Build.Model} hardware={Build.Hardware} RAM {mi.TotalMem / 1048576} MB memoryClass {am.MemoryClass} MB");
        _rec.Header("android", $"{Build.VERSION.Release} (SDK {(int)Build.VERSION.SdkInt})");
        _rec.Header("webview", WebView.CurrentWebViewPackage is { } p ? $"{p.PackageName} {p.VersionName}" : "unknown");
    }

    // ---------------- UI ----------------

    private void BuildUi()
    {
        float dp = Resources!.DisplayMetrics!.Density;
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.Black);

        _status = new TextView(this) { Text = "Connecting to the app..." };
        _status.SetTextColor(Color.LightGray);
        _status.Gravity = GravityFlags.Center;
        _root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _hud = new TextView(this) { Visibility = ViewStates.Gone };
        _hud.SetTextColor(Color.Argb(230, 255, 255, 120));
        _hud.SetBackgroundColor(Color.Argb(140, 0, 0, 0));
        _hud.SetTextSize(Android.Util.ComplexUnitType.Sp, 10);
        _hud.SetPadding((int)(4 * dp), (int)(2 * dp), (int)(4 * dp), (int)(2 * dp));
        _root.AddView(_hud, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Left));

        LinearLayout buttons = new(this) { Orientation = Orientation.Horizontal };
        _loginButton = SmallButton("Log in", RequestAutoLogin, dp);
        _loginButton.Visibility = ViewStates.Gone;
        buttons.AddView(_loginButton);
        buttons.AddView(SmallButton("HUD", ToggleHud, dp));
        buttons.AddView(SmallButton("Report", ShowReport, dp));
        buttons.AddView(SmallButton("X", () => CloseView("closed by the user"), dp));
        _root.AddView(buttons, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Right));

        SetContentView(_root);
    }

    private Button SmallButton(string text, Action onClick, float dp)
    {
        Button b = new(this) { Text = text };
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 11);
        b.SetMinWidth(0);
        b.SetMinimumWidth(0);
        b.SetMinHeight(0);
        b.SetMinimumHeight(0);
        b.SetPadding((int)(10 * dp), (int)(4 * dp), (int)(10 * dp), (int)(4 * dp));
        b.Alpha = 0.6f;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void GoFullScreen()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Window!.InsetsController?.Hide(WindowInsets.Type.SystemBars());
            if (Window.InsetsController is { } c)
                c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    private void ToggleHud() => _hud.Visibility = _hud.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;

    private void Tick()
    {
        if (_shutDown)
            return;
        _hud.Text = _rec.Hud() + (_channel is { } c ? $" | sockets {c.ActiveConnections} open, {c.BytesFromServer / 1024} KB in" : string.Empty);
        _main.PostDelayed(Tick, 2000);
    }

    private void ShowReport()
    {
        string text = BuildFinalReport();
        TextView tv = new(this) { Text = text, Typeface = Typeface.Monospace };
        tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 9);
        tv.SetTextIsSelectable(true);
        ScrollView sv = new(this);
        sv.AddView(tv);
        new AlertDialog.Builder(this)
            .SetTitle("Game view report")!
            .SetView(sv)!
            .SetPositiveButton("Share", (_, _) => Share(text))!
            .SetNegativeButton("Back", (_, _) => { })!
            .Show();
    }

    private void Share(string text)
    {
        Intent send = new(Intent.ActionSend);
        send.SetType("text/plain");
        send.PutExtra(Intent.ExtraSubject, "VibeSkua game view report");
        send.PutExtra(Intent.ExtraText, text);
        StartActivity(Intent.CreateChooser(send, "Share report"));
    }

    private string BuildFinalReport()
    {
        StringBuilder sb = new(_rec.BuildReport());
        sb.AppendLine();
        sb.AppendLine("== Socket bridge ==");
        sb.AppendLine($"  allowlist {_allowlist.Endpoints.Count} endpoints: {string.Join(" ", _allowlist.Endpoints.Select(e => e.ToString()).Order())}");
        if (_channel is { } c)
        {
            string rejected = string.Join(", ", c.Rejections.Select(kv => $"{kv.Key} x{kv.Value}"));
            sb.AppendLine($"  connections {c.TotalConnections} (open {c.ActiveConnections}), {c.BytesToServer} B up, {c.BytesFromServer} B down, rejected: {(rejected.Length == 0 ? "none" : rejected)}");
        }
        return sb.ToString();
    }

    // ---------------- Link to the engine process ----------------

    private sealed class Link : Java.Lang.Object, IServiceConnection
    {
        private readonly GameViewActivity _owner;

        public Link(GameViewActivity owner)
        {
            _owner = owner;
        }

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is null)
                return;
            _owner._engine = new Messenger(service);
            Bundle b = new();
            b.PutString(GameViewLinkProtocol.KeySession, _owner._session);
            Message hello = GameViewLinkProtocol.Make(GameViewLinkProtocol.Hello, b);
            hello.ReplyTo = new Messenger(new FromEngine(_owner));
            try
            {
                _owner._engine.Send(hello);
            }
            catch (RemoteException)
            {
                _owner.ShowStatusAndClose("The app's engine could not be reached.");
            }
        }

        public void OnServiceDisconnected(ComponentName? name)
        {
            // The engine process died: nobody can take the account back right now; close (the next app start logs in).
            _owner._engine = null;
            _owner.CloseView("the app's engine process ended");
        }
    }

    private sealed class FromEngine : Handler
    {
        private readonly GameViewActivity _owner;

        public FromEngine(GameViewActivity owner)
            : base(Looper.MainLooper!)
        {
            _owner = owner;
        }

        public override void HandleMessage(Message msg)
        {
            if (msg.SendingUid != Process.MyUid())
                return;
            switch (msg.What)
            {
                case GameViewLinkProtocol.HelloOk:
                    _owner.OnHello(msg.Data?.GetString(GameViewLinkProtocol.KeyOptions), msg.Data?.GetString(GameViewLinkProtocol.KeyAccount));
                    break;
                case GameViewLinkProtocol.Rejected:
                    _owner.ShowStatusAndClose("This game view is no longer current.");
                    break;
                case GameViewLinkProtocol.Close:
                    _owner.CloseView(msg.Data?.GetString(GameViewLinkProtocol.KeyReason) ?? "closed from the app");
                    break;
                default:
                    _owner.OnEngineMessage(msg);
                    break;
            }
        }
    }

    /// <summary>Messages beyond the basic hand-shake: the stored login for auto-login.</summary>
    private void OnEngineMessage(Message msg)
    {
        switch (msg.What)
        {
            case GameViewLinkProtocol.CredentialsOk:
                string? user = msg.Data?.GetString(GameViewLinkProtocol.KeyUser);
                string? password = msg.Data?.GetString(GameViewLinkProtocol.KeyPassword);
                msg.Data?.Clear();
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || _appPort is null || _closing)
                {
                    Toast.MakeText(this, "Auto-login is not possible right now; log in on the game's screen.", ToastLength.Long)?.Show();
                    return;
                }
                // Redacted from everything the report keeps from now on; handed to the page only over its private port.
                _rec.AddSecret(password);
                _rec.Event("autologin_requested");
                SendToPage("L|" + Base64(user) + "|" + Base64(password));
                break;
            case GameViewLinkProtocol.NoCredentials:
                _loginButton.Visibility = ViewStates.Gone;
                Toast.MakeText(this, "No stored login for auto-login; log in on the game's screen.", ToastLength.Long)?.Show();
                break;
        }
    }

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private void SendToEngine(int what, Bundle data)
    {
        data.PutString(GameViewLinkProtocol.KeySession, _session);
        try
        {
            _engine?.Send(GameViewLinkProtocol.Make(what, data));
        }
        catch (RemoteException)
        {
        }
    }

    private void OnHello(string? optionsJson, string? autoLoginAccount)
    {
        if (_prepared || _closing)
            return;
        _prepared = true;
        _options = GameViewOptions.FromJson(optionsJson);
        _autoLoginAccount = autoLoginAccount;
        _rec.Header("settings", _options.Describe());
        _hud.Visibility = _options.ShowHud ? ViewStates.Visible : ViewStates.Gone;
        _rec.StageChanged += stage => _main.Post(() => OnStage(stage));
        _status.Text = "Fetching the server list...";
        _ = Task.Run(PrepareAsync);
        _guard = new GameViewMemoryGuard(_options.MemoryLimitMb);
        // Every 3 s: PSS for the guard (cheap); every 10 s a full sample for the report.
        _memTimer = new System.Threading.Timer(_ => OnMemoryTimer(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        _main.PostDelayed(Tick, 2000);
    }

    private void OnStage(string stage)
    {
        if (_closing)
            return;
        Bundle b = new();
        b.PutString(GameViewLinkProtocol.KeyStage, stage);
        SendToEngine(GameViewLinkProtocol.Stage, b);
        OnStageForLogin(stage);
    }

    /// <summary>"Log in as ACCOUNT" is offered on the login screen when a password is remembered (the app's vault).</summary>
    private void OnStageForLogin(string stage)
    {
        if (_autoLoginAccount is null)
            return;
        _loginButton.Text = "Log in as " + _autoLoginAccount;
        _loginButton.Visibility = stage == "login_screen" ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Asks the engine process for the stored login (decrypted there, sent back over the private link).</summary>
    private void RequestAutoLogin()
    {
        _loginButton.Visibility = ViewStates.Gone;
        SendToEngine(GameViewLinkProtocol.Credentials, new Bundle());
    }

    // ---------------- Startup: server list, bridge, WebView ----------------

    private async Task PrepareAsync()
    {
        try
        {
            _allowlist = await GameApi.FetchServerListAsync(_http, CancellationToken.None);
            _rec.Event("server_list", $"{_allowlist.Endpoints.Count} endpoints");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException)
        {
            // The view still opens (the login works over HTTPS) but the game cannot connect to a server.
            _rec.Event("server_list_failed", ex.GetType().Name);
        }

        _bridgeThread = new HandlerThread("gameview-bridge");
        _bridgeThread.Start();
        _bridgeHandler = new Handler(_bridgeThread.Looper!);
        _channel = new ChannelBridge(_allowlist, SendToPage, null, OnBridgeEvent);
        _configJson = _options.PageConfigJson(_allowlist.Endpoints.Select(e => (e, ChannelBridge.ProxyUrl(e))));
        _main.Post(CreateWebView);
    }

    private void OnBridgeEvent(BridgeEvent e)
    {
        _rec.Event("bridge_" + e.Kind, e.Detail);
        if (e.Kind == "connected")
            _rec.Mark("server_socket_connected", e.Detail);
    }

    private void CreateWebView()
    {
        if (_closing)
            return;
        _status.Text = "Loading...";
        _rec.Mark("webview_create_start");
        WebView web = new(this);
        WebSettings s = web.Settings!;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true; // Ruffle keeps the game's SharedObjects in localStorage (cleared on open and close)
        s.MediaPlaybackRequiresUserGesture = false;
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.JavaScriptCanOpenWindowsAutomatically = false;
        s.SetSupportMultipleWindows(false);
        s.SetSupportZoom(false);
        s.BuiltInZoomControls = false;
        s.TextZoom = 100;
        s.SaveFormData = false;
        s.MixedContentMode = MixedContentHandling.NeverAllow;
        web.SetRendererPriorityPolicy(RendererPriority.Important, false);
        web.SetBackgroundColor(Color.Black);
        web.SetWebViewClient(new Client(this));
        web.SetWebChromeClient(new Chrome(this));
        GuardServiceWorkers();
        // The previous view may have been killed before it could clear the HTTP cache (storage was cleared in OnCreate).
        web.ClearCache(true);
        _root.AddView(web, 0, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _web = web;
        _status.Visibility = ViewStates.Gone;
        web.LoadUrl(RequestPolicy.PageUrl);
        _rec.Mark("webview_load_url");
    }

    /// <summary>Gives the page its end of the private socket channel (only to the page's origin).</summary>
    private void PostChannel(WebView view)
    {
        if (_channel is null || _appPort is not null)
            return;
        WebMessagePort[] ports = view.CreateWebMessageChannel();
        _appPort = ports[0];
        _appPort.SetWebMessageCallback(new PortCallback(this), _bridgeHandler);
        view.PostWebMessage(new WebMessage("vibeskua-bridge", [ports[1]]), Uri.Parse("https://" + RequestPolicy.GameHost)!);
        _rec.Event("bridge_channel_posted");
    }

    private void SendToPage(string message) =>
        _main.Post(() =>
        {
            try
            {
                _appPort?.PostMessage(new WebMessage(message));
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or ObjectDisposedException)
            {
                _rec.Event("bridge_post_failed", ex.GetType().Name);
            }
        });

    private sealed class PortCallback : WebMessagePort.WebMessageCallback
    {
        private readonly GameViewActivity _owner;

        public PortCallback(GameViewActivity owner)
        {
            _owner = owner;
        }

        public override void OnMessage(WebMessagePort? port, WebMessage? message)
        {
            if (message?.Data is { } data)
                _owner._channel?.Receive(data);
        }
    }

    // ---------------- WebView callbacks ----------------

    private sealed class Client : WebViewClient
    {
        private readonly GameViewActivity _owner;

        public Client(GameViewActivity owner)
        {
            _owner = owner;
        }

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? string.Empty;
            RequestDecision d = RequestPolicy.Decide(url, request?.Method ?? "GET", request?.RequestHeaders);
            switch (d.Action)
            {
                case RequestAction.PassThrough:
                    return null;
                case RequestAction.ServeLocal:
                    return _owner.Local(d.LocalPath!);
                case RequestAction.AppFetch:
                    return _owner.AppFetch(d.FetchUri!, request?.Method ?? "GET");
                default:
                    _owner._rec.Blocked(RequestPolicy.BlockKey(url) + " (" + d.Reason + ")");
                    return Empty(403, "Forbidden");
            }
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? string.Empty;
            if (url == RequestPolicy.PageUrl)
                return false;
            _owner._rec.Event("navigation_blocked", RequestPolicy.BlockKey(url));
            return true;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (view is null || url != RequestPolicy.PageUrl)
                return;
            _owner._rec.Mark("page_finished");
            _owner.PostChannel(view);
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request is null)
                return;
            _owner._rec.Event("load_error", $"{RequestPolicy.BlockKey(request.Url?.ToString() ?? string.Empty)} {error?.ErrorCode} {error?.Description}{(request.IsForMainFrame ? " (main frame)" : string.Empty)}");
        }

        public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? response)
        {
            if (request?.Url is not { } u)
                return;
            // Path only (query strings can carry tokens).
            _owner._rec.Event("http_error", $"{response?.StatusCode} {u.Scheme}://{u.Host}{u.Path}");
        }

        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            bool crashed = detail?.DidCrash() ?? false;
            int prio = detail?.RendererPriorityAtExit() ?? -1;
            _owner._rec.RendererGone(crashed, prio, prio switch { 0 => "waived", 1 => "bound", 2 => "important", _ => "unknown" });
            _owner.OnRendererGone(view, crashed);
            return true; // handled: this process keeps running and closes the view cleanly
        }
    }

    /// <summary>
    /// Service workers' requests never reach the WebViewClient. The game registers none and the page's CSP
    /// (worker-src 'none') refuses new ones; one registered before (storage is wiped on open and close) gets no network
    /// at all and the page's request rules (<see cref="RequestPolicy.DecideServiceWorker"/>).
    /// </summary>
    private void GuardServiceWorkers()
    {
        try
        {
            ServiceWorkerController swc = ServiceWorkerController.Instance;
            ServiceWorkerWebSettings sws = swc.ServiceWorkerWebSettings;
            sws.AllowContentAccess = false;
            sws.AllowFileAccess = false;
            sws.BlockNetworkLoads = true;
            swc.SetServiceWorkerClient(new WorkerClient(this));
        }
        catch (Java.Lang.Exception ex)
        {
            _rec.Event("service_worker_guard_failed", ex.GetType().Name);
        }
    }

    private sealed class WorkerClient : ServiceWorkerClient
    {
        private readonly GameViewActivity _owner;

        public WorkerClient(GameViewActivity owner)
        {
            _owner = owner;
        }

        public override WebResourceResponse? ShouldInterceptRequest(IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? string.Empty;
            RequestDecision d = RequestPolicy.DecideServiceWorker(url, request?.Method ?? "GET", request?.RequestHeaders);
            switch (d.Action)
            {
                case RequestAction.PassThrough:
                    return null; // and its network loads are blocked (GuardServiceWorkers)
                case RequestAction.ServeLocal:
                    return _owner.Local(d.LocalPath!);
                default:
                    _owner._rec.Blocked(RequestPolicy.BlockKey(url) + " (" + d.Reason + ")");
                    return Empty(403, "Forbidden");
            }
        }
    }

    private sealed class Chrome : WebChromeClient
    {
        private readonly GameViewActivity _owner;

        public Chrome(GameViewActivity owner)
        {
            _owner = owner;
        }

        public override bool OnConsoleMessage(ConsoleMessage? m)
        {
            if (m is null)
                return true;
            _owner._rec.Console(m.Message() ?? string.Empty, m.InvokeMessageLevel()?.Name() ?? "LOG");
            return true; // never written to logcat
        }
    }

    private WebResourceResponse Local(string path)
    {
        // Every local response carries the page's Content-Security-Policy (RequestPolicy.ContentSecurityPolicy).
        Dictionary<string, string> headers = RequestPolicy.LocalResponseHeaders();
        string mime = RequestPolicy.MimeType(path);
        string? encoding = mime.StartsWith("text/", StringComparison.Ordinal) || mime == "application/json" ? "utf-8" : null;
        if (path == "config.json")
            return new WebResourceResponse(mime, encoding, 200, "OK", headers, new MemoryStream(Encoding.UTF8.GetBytes(_configJson)));
        try
        {
            Stream s = Assets!.Open("gameview/" + path);
            return new WebResourceResponse(mime, encoding, 200, "OK", headers, s);
        }
        catch (Java.IO.IOException)
        {
            return Empty(404, "Not Found");
        }
    }

    /// <summary>
    /// A cross-origin request the client makes by itself (quest tracking on www.aq.com): fetched by the app here, on
    /// the WebView's request thread, and answered with CORS headers for the page's origin only.
    /// </summary>
    private WebResourceResponse AppFetch(System.Uri uri, string method)
    {
        Dictionary<string, string> headers = new()
        {
            ["Cache-Control"] = "no-store",
            ["Access-Control-Allow-Origin"] = RequestPolicy.PageOrigin,
            ["Access-Control-Allow-Credentials"] = "true",
            ["Vary"] = "Origin",
        };
        if (method == "OPTIONS")
        {
            headers["Access-Control-Allow-Methods"] = "GET";
            return new WebResourceResponse("text/plain", "utf-8", 204, "No Content", headers, new MemoryStream());
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        (int status, string type, byte[] body) = GameApi.FetchAllowedAsync(_http, uri, timeout.Token).GetAwaiter().GetResult();
        _rec.Event("app_fetch", $"{RequestPolicy.BlockKey(uri.AbsoluteUri)} {status}");
        if (status is < 100 or > 599 or (>= 300 and < 400))
            status = 502; // WebResourceResponse refuses redirects and invalid codes
        return new WebResourceResponse(type, null, status, status is >= 200 and < 300 ? "OK" : "Error", headers, new MemoryStream(body));
    }

    private static WebResourceResponse Empty(int status, string reason) =>
        new("text/plain", "utf-8", status, reason, RequestPolicy.LocalResponseHeaders(), new MemoryStream());

    private void OnRendererGone(WebView? view, bool crashed)
    {
        _appPort = null;
        if (view is not null)
        {
            _root.RemoveView(view);
            view.Destroy();
        }
        if (_web == view)
            _web = null;
        Guard(_guard.RendererGone(crashed));
    }

    // ---------------- Memory ----------------

    private void SampleMemory()
    {
        try
        {
            Debug.MemoryInfo mi = new();
            Debug.GetMemoryInfo(mi);
            double rssMb = 0;
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    rssMb = double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024;
            }
            Java.Lang.Runtime rt = Java.Lang.Runtime.GetRuntime()!;
            ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
            ActivityManager.MemoryInfo sys = new();
            am.GetMemoryInfo(sys);
            NativeMemorySample sample = new(mi.TotalPss / 1024.0, rssMb, (rt.TotalMemory() - rt.FreeMemory()) / 1048576.0,
                Debug.NativeHeapAllocatedSize / 1048576.0, sys.AvailMem / 1048576.0, sys.LowMemory);
            _rec.NativeMemory(sample);
            OnMemorySample(sample);
        }
        catch (Exception ex) when (ex is IOException or FormatException or Java.Lang.Exception)
        {
            _rec.Event("memory_sample_failed", ex.GetType().Name);
        }
    }

    private void OnMemoryTimer()
    {
        if (_shutDown)
            return;
        if (_memTicks++ % 3 == 0)
        {
            SampleMemory();
        }
        else
        {
            try
            {
                Guard(_guard.Sample(GameViewMemoryGuard.ParseSmapsRollupPss(File.ReadAllText("/proc/self/smaps_rollup"))));
            }
            catch (IOException)
            {
                // Before Android 10 there is no smaps_rollup: the full sample every 9 s guards alone.
            }
        }
        // The close runs on the main thread; if that is saturated (a runaway renderer), give up waiting and end the
        // process: its sockets drop and the engine process takes the account back after its unconfirmed wait.
        if (_guardDeadline != 0 && SystemClock.ElapsedRealtime() > _guardDeadline && !_shutDown)
        {
            Bundle b = new();
            b.PutString(GameViewLinkProtocol.KeyReason, _guard.Reason ?? "the game view stopped responding");
            b.PutBoolean(GameViewLinkProtocol.KeyClosed, false);
            SendToEngine(GameViewLinkProtocol.Closing, b);
            Process.KillProcess(Process.MyPid());
        }
    }

    private void OnMemorySample(NativeMemorySample sample) => Guard(_guard.Sample(sample.PssMB));

    /// <summary>Acts on the memory guard's verdict (any thread).</summary>
    private void Guard(MemoryGuardVerdict verdict)
    {
        if (verdict == MemoryGuardVerdict.Warn)
        {
            _rec.Event("memory_warning", $"PSS over {GameViewMemoryGuard.WarnFraction:P0} of {_guard.LimitMb} MB");
            _main.Post(() => Toast.MakeText(this, $"The game view is close to its memory limit ({_guard.LimitMb} MB).", ToastLength.Short)?.Show());
        }
        else if (verdict == MemoryGuardVerdict.Close && Interlocked.CompareExchange(ref _guardDeadline, SystemClock.ElapsedRealtime() + 8000, 0) == 0)
        {
            string reason = _guard.Reason!;
            _rec.Event("memory_guard", reason);
            _main.Post(() =>
            {
                _status.Visibility = ViewStates.Visible;
                _status.Text = "Closing: " + reason + ". Back to the bot.";
                CloseView(reason);
            });
        }
    }

    public override void OnTrimMemory([GeneratedEnum] TrimMemory level)
    {
        base.OnTrimMemory(level);
        _rec.Event("trim_memory", level.ToString());
    }

    // ---------------- Lifecycle ----------------

    protected override void OnPause()
    {
        base.OnPause();
        _web?.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _web?.OnResume();
        GoFullScreen();
    }

    protected override void OnStart()
    {
        base.OnStart();
        if (_backgroundClose is not null)
            _main.RemoveCallbacks(_backgroundClose);
        _backgroundClose = null;
    }

    protected override void OnStop()
    {
        base.OnStop();
        if (_closing || IsFinishing)
            return;
        _backgroundClose = () => CloseView("the game view was in the background for 2 minutes");
        _main.PostDelayed(_backgroundClose, (long)BackgroundLimit.TotalMilliseconds);
    }

    public override void OnBackPressed() => CloseView("closed by the user");

    private void ShowStatusAndClose(string text)
    {
        _status.Visibility = ViewStates.Visible;
        _status.Text = text;
        CloseView(text);
    }

    /// <summary>
    /// Back to headless: the game logs out, the sockets close (at most 3 s), then everything is torn down, the engine
    /// process is told and this process ends. Idempotent.
    /// </summary>
    private void CloseView(string reason)
    {
        if (_closing)
            return;
        _closing = true;
        _rec.Event("closing", reason);
        _status.Visibility = ViewStates.Visible;
        _status.Text = "Logging out of the game view...";
        _status.BringToFront();
        Toast.MakeText(this, "Game view closed: " + reason, ToastLength.Long)?.Show();
        bool asked = false;
        if (_web is { } web)
        {
            try
            {
                web.EvaluateJavascript("window.vgame && window.vgame.logout()", null);
                asked = true;
            }
            catch (Java.Lang.Exception)
            {
            }
        }
        long deadline = SystemClock.ElapsedRealtime() + (asked ? 3000 : 0);
        void WaitForSockets()
        {
            if ((_channel is null || _channel.Idle) || SystemClock.ElapsedRealtime() >= deadline)
                FinishClose(reason);
            else
                _main.PostDelayed(WaitForSockets, 100);
        }
        _main.PostDelayed(WaitForSockets, asked ? 300 : 0);
    }

    private void FinishClose(string reason)
    {
        bool socketsClosed = _channel is null || _channel.Idle;
        Shutdown();
        Bundle b = new();
        b.PutString(GameViewLinkProtocol.KeyReason, reason);
        // The sockets are all closed now (the bridge was disposed); "closed" says whether the game closed them itself
        // after its logout, which tells the engine the server has the session ended already.
        b.PutBoolean(GameViewLinkProtocol.KeyClosed, socketsClosed);
        b.PutString(GameViewLinkProtocol.KeySummary, _rec.Summary() + "; " + _options.Describe());
        SendToEngine(GameViewLinkProtocol.Closing, b);
        if (_link is not null)
        {
            try
            {
                UnbindService(_link);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }
            _link = null;
        }
        Finish();
        // The whole process goes: Ruffle's and the WebView's memory are given back at once.
        _main.PostDelayed(() => Process.KillProcess(Process.MyPid()), 700);
    }

    private void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;
        _memTimer?.Dispose();
        try
        {
            _appPort?.Close();
        }
        catch (Java.Lang.Exception)
        {
        }
        _appPort = null;
        if (_web is { } w)
        {
            _root.RemoveView(w);
            w.StopLoading();
            try
            {
                w.ClearCache(true); // the HTTP cache (shared by every WebView of this process)
            }
            catch (Java.Lang.Exception)
            {
            }
            w.Destroy();
            _web = null;
        }
        _ = _channel?.DisposeAsync().AsTask();
        _bridgeThread?.QuitSafely();
        _http.Dispose();
        ClearWebData();
    }

    /// <summary>
    /// Nothing the game stores (its Save boxes use localStorage, cookies) survives: wiped on open and close, and the
    /// HTTP cache with it (through the WebView: when it is created and before it is destroyed). Android has no call that
    /// removes service worker registrations alone: WebStorage.deleteAllData clears the origin's storage, the page
    /// unregisters any it finds, and their network loads are blocked (<see cref="GuardServiceWorkers"/>).
    /// </summary>
    private static void ClearWebData()
    {
        try
        {
            WebStorage.Instance?.DeleteAllData();
            CookieManager.Instance?.RemoveAllCookies(null);
            CookieManager.Instance?.Flush();
        }
        catch (Java.Lang.Exception)
        {
            // best effort
        }
    }

    protected override void OnDestroy()
    {
        if (!_closing)
        {
            // Destroyed without our close (the system finishing it): still log out and tell the engine.
            _closing = true;
            try
            {
                _web?.EvaluateJavascript("window.vgame && window.vgame.logout()", null);
            }
            catch (Java.Lang.Exception)
            {
            }
            FinishCloseOnDestroy();
        }
        base.OnDestroy();
    }

    private void FinishCloseOnDestroy()
    {
        bool socketsClosed = _channel is null || _channel.Idle;
        Shutdown();
        Bundle b = new();
        b.PutString(GameViewLinkProtocol.KeyReason, "the game view was destroyed");
        b.PutBoolean(GameViewLinkProtocol.KeyClosed, socketsClosed);
        SendToEngine(GameViewLinkProtocol.Closing, b);
        if (_link is not null)
        {
            try
            {
                UnbindService(_link);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
            }
            _link = null;
        }
    }
}
