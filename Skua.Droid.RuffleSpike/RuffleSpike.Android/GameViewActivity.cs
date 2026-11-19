using System.Globalization;
using System.Text;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using RuffleSpike.Core;
using Debug = Android.OS.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;
using Uri = Android.Net.Uri;

namespace RuffleSpike.Android;

/// <summary>
/// The game view: a full-screen WebView showing https://game.aq.com/__vibeskua__/index.html, answered by the app
/// (see <see cref="RequestPolicy"/>), with Ruffle running skua.swf or Loader4.swf. Records everything into a
/// <see cref="SpikeRecorder"/>; the report is saved when the view closes.
/// </summary>
[Activity(Label = "Game view", ScreenOrientation = ScreenOrientation.SensorLandscape, HardwareAccelerated = true,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    WindowSoftInputMode = SoftInput.AdjustPan | SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed class GameViewActivity : Activity
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SpikeSettings _settings = new();
    private SpikeRecorder _rec = null!;
    private BridgeSecret _secret = null!;
    private FrameLayout _root = null!;
    private TextView _hud = null!, _status = null!;
    private WebView? _web;
    private string _configJson = "{}";
    private ServerAllowlist _allowlist = new([]);
    private ChannelBridge? _channel;
    private SocketBridge? _loopback;
    private WebMessagePort? _appPort;
    private Handler _main = null!;
    private HandlerThread? _bridgeThread;
    private Handler? _bridgeHandler;
    private System.Threading.Timer? _memTimer;
    private bool _closed, _soakDone;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _main = new Handler(Looper.MainLooper!);
        _settings = SpikeApp.Instance.LoadSettings();
        _secret = BridgeSecret.Create();
        _rec = new SpikeRecorder(() => _clock.Elapsed.TotalSeconds, _secret.Value);
        Header();
        ClearWebData("view opened");
        BuildUi();
        GoFullScreen();
        _ = Task.Run(PrepareAsync);
        _memTimer = new System.Threading.Timer(_ => SampleMemory(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _main.PostDelayed(Tick, 2000);
    }

    private void Header()
    {
        ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
        ActivityManager.MemoryInfo mi = new();
        am.GetMemoryInfo(mi);
        string soc = OperatingSystem.IsAndroidVersionAtLeast(31) ? $" soc={Build.SocManufacturer} {Build.SocModel}" : "";
        _rec.Header("time", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        _rec.Header("device", $"{Build.Manufacturer} {Build.Model} ({Build.Device}) hardware={Build.Hardware}{soc} RAM {mi.TotalMem / 1048576} MB memoryClass {am.MemoryClass} MB");
        _rec.Header("android", $"{Build.VERSION.Release} (SDK {(int)Build.VERSION.SdkInt}) abi {string.Join(",", Build.SupportedAbis ?? [])}");
        _rec.Header("webview", WebView.CurrentWebViewPackage is { } p ? $"{p.PackageName} {p.VersionName}" : "unknown");
        _rec.Header("app", $"{PackageName} {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName}");
        _rec.Header("settings", _settings.Describe());
        _rec.Header("previous exits", "\n" + SpikeApp.Instance.RecentExits(3));
    }

    // ---------------- UI ----------------

    private void BuildUi()
    {
        float dp = Resources!.DisplayMetrics!.Density;
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.Black);

        _status = new TextView(this) { Text = "Fetching the server list..." };
        _status.SetTextColor(Color.LightGray);
        _status.Gravity = GravityFlags.Center;
        _root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _hud = new TextView(this) { Visibility = _settings.ShowHud ? ViewStates.Visible : ViewStates.Gone };
        _hud.SetTextColor(Color.Argb(230, 255, 255, 120));
        _hud.SetBackgroundColor(Color.Argb(140, 0, 0, 0));
        _hud.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10);
        _hud.SetPadding((int)(4 * dp), (int)(2 * dp), (int)(4 * dp), (int)(2 * dp));
        _root.AddView(_hud, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Left));

        LinearLayout buttons = new(this) { Orientation = Orientation.Horizontal };
        buttons.AddView(SmallButton("Report", ShowReport, dp));
        buttons.AddView(SmallButton("X", () => Close("closed by the user"), dp));
        _root.AddView(buttons, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Right));

        SetContentView(_root);
    }

    private Button SmallButton(string text, Action onClick, float dp)
    {
        Button b = new(this) { Text = text };
        b.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11);
        b.SetMinWidth(0); b.SetMinimumWidth(0); b.SetMinHeight(0); b.SetMinimumHeight(0);
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
            if (Window.InsetsController is { } c) c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    private void Tick()
    {
        if (_closed) return;
        _hud.Text = _rec.Hud() + BridgeLine();
        if (_settings.SoakMinutes > 0 && !_soakDone && _rec.MarkTime("in_game") is double inGame
            && _rec.Now - inGame >= _settings.SoakMinutes * 60)
        {
            _soakDone = true;
            _rec.Mark("soak_complete", $"{_settings.SoakMinutes} min in game");
            string path = SpikeApp.Instance.SaveReport(_rec.BuildReport());
            Toast.MakeText(this, "Soak finished, report saved: " + System.IO.Path.GetFileName(path), ToastLength.Long)!.Show();
        }
        _main.PostDelayed(Tick, 2000);
    }

    private string BridgeLine()
    {
        if (_channel is { } c) return $" | sockets {c.ActiveConnections} open, {c.TotalConnections} total, {c.BytesFromServer / 1024} KB in";
        if (_loopback is { } l) return $" | sockets {l.ActiveConnections} open, {l.TotalConnections} total, {l.BytesFromServer / 1024} KB in";
        return "";
    }

    private void ShowReport()
    {
        string text = BuildFinalReport();
        TextView tv = new(this) { Text = text, Typeface = Typeface.Monospace };
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 9);
        tv.SetTextIsSelectable(true);
        ScrollView sv = new(this);
        sv.AddView(tv);
        new AlertDialog.Builder(this)
            .SetTitle("Report so far")!
            .SetView(sv)!
            .SetPositiveButton("Share", (_, _) => MainActivity.Share(this, text))!
            .SetNeutralButton("Copy", (_, _) => MainActivity.Copy(this, text))!
            .SetNegativeButton("Back", (_, _) => { })!
            .Show();
    }

    private string BuildFinalReport()
    {
        StringBuilder sb = new(_rec.BuildReport());
        sb.AppendLine();
        sb.AppendLine("== Socket bridge ==");
        sb.AppendLine($"  transport {_settings.Transport}, allowlist {_allowlist.Endpoints.Count} endpoints: {string.Join(" ", _allowlist.Endpoints.Select(e => e.ToString()).Order())}");
        if (_channel is { } c)
            sb.AppendLine($"  connections {c.TotalConnections} (open {c.ActiveConnections}), {c.BytesToServer} B up, {c.BytesFromServer} B down, rejected: {Rejections(c.Rejections)}");
        if (_loopback is { } l)
            sb.AppendLine($"  loopback port {l.Port}, connections {l.TotalConnections} (open {l.ActiveConnections}), {l.BytesToServer} B up, {l.BytesFromServer} B down, rejected: {Rejections(l.Rejections)}");
        return sb.ToString();
    }

    private static string Rejections(IEnumerable<KeyValuePair<string, int>> r)
    {
        string s = string.Join(", ", r.Select(kv => $"{kv.Key} x{kv.Value}"));
        return s.Length == 0 ? "none" : s;
    }

    // ---------------- Startup: server list, bridge, WebView ----------------

    private async Task PrepareAsync()
    {
        try
        {
            _allowlist = await GameApi.FetchServerListAsync(_http, CancellationToken.None);
            _rec.Event("server_list", $"{_allowlist.Endpoints.Count} endpoints");
        }
        catch (Exception ex)
        {
            // The view still opens (login works over HTTPS) but the game cannot connect to a server.
            _rec.Event("server_list_failed", $"{ex.GetType().Name}: {ex.Message}");
        }

        List<(GameEndpoint, string)> proxies = [];
        try
        {
            if (_settings.Transport == "loopback")
            {
                _loopback = new SocketBridge(_secret, _allowlist, null, OnBridgeEvent);
                _loopback.Start();
                proxies.AddRange(_allowlist.Endpoints.Select(e => (e, _loopback.ProxyUrl(e))));
            }
            else
            {
                _bridgeThread = new HandlerThread("bridge-channel");
                _bridgeThread.Start();
                _bridgeHandler = new Handler(_bridgeThread.Looper!);
                _channel = new ChannelBridge(_allowlist, SendToPage, null, OnBridgeEvent);
                proxies.AddRange(_allowlist.Endpoints.Select(e => (e, ChannelBridge.ProxyUrl(e))));
            }
        }
        catch (Exception ex)
        {
            _rec.Event("bridge_start_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        _configJson = _settings.PageConfigJson(proxies);
        _main.Post(CreateWebView);
    }

    private void OnBridgeEvent(BridgeEvent e)
    {
        _rec.Event("bridge_" + e.Kind, e.Detail);
        if (e.Kind == "connected") _rec.Mark("server_socket_connected", e.Detail);
    }

    private void CreateWebView()
    {
        if (_closed) return;
        _status.Text = "Loading...";
        _rec.Mark("webview_create_start");
        if (string.Equals(Config(), "Debug", StringComparison.Ordinal)) WebView.SetWebContentsDebuggingEnabled(true);
        WebView web = new(this);
        WebSettings s = web.Settings!;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;          // Ruffle keeps the game's SharedObjects in localStorage
        s.MediaPlaybackRequiresUserGesture = false;
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.JavaScriptCanOpenWindowsAutomatically = false;
        s.SetSupportMultipleWindows(false);
        s.SetSupportZoom(false);
        s.BuiltInZoomControls = false;
        s.TextZoom = 100;
        s.MixedContentMode = _settings.AllowMixedContent ? MixedContentHandling.CompatibilityMode : MixedContentHandling.NeverAllow;
        web.SetRendererPriorityPolicy(RendererPriority.Important, false);
        web.SetBackgroundColor(Color.Black);
        web.SetWebViewClient(new Client(this));
        web.SetWebChromeClient(new Chrome(this));
        _root.AddView(web, 0, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _web = web;
        _status.Visibility = ViewStates.Gone;
        web.LoadUrl(RequestPolicy.PageUrl);
        _rec.Mark("webview_load_url");
    }

    private static string Config()
    {
        var a = typeof(GameViewActivity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "Configuration");
        return a?.Value ?? "?";
    }

    /// <summary>Gives the page its end of the private socket channel (only to the page's origin).</summary>
    private void PostChannel(WebView view)
    {
        if (_channel is null || _appPort is not null) return;
        WebMessagePort[] ports = view.CreateWebMessageChannel();
        _appPort = ports[0];
        _appPort.SetWebMessageCallback(new PortCallback(this), _bridgeHandler);
        view.PostWebMessage(new WebMessage("vibeskua-bridge", [ports[1]]), Uri.Parse("https://" + RequestPolicy.GameHost)!);
        _rec.Event("bridge_channel_posted");
    }

    private void SendToPage(string message) =>
        _main.Post(() =>
        {
            try { _appPort?.PostMessage(new WebMessage(message)); }
            catch (Exception ex) { _rec.Event("bridge_post_failed", ex.GetType().Name); }
        });

    private sealed class PortCallback(GameViewActivity owner) : WebMessagePort.WebMessageCallback
    {
        public override void OnMessage(WebMessagePort? port, WebMessage? message)
        {
            if (message?.Data is { } data) owner._channel?.Receive(data);
        }
    }

    // ---------------- WebView callbacks ----------------

    private sealed class Client(GameViewActivity owner) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? "";
            RequestDecision d = RequestPolicy.Decide(url);
            switch (d.Action)
            {
                case RequestAction.PassThrough:
                    return null;
                case RequestAction.ServeLocal:
                    return owner.Local(d.LocalPath!);
                default:
                    owner._rec.Blocked(RequestPolicy.BlockKey(url) + " (" + d.Reason + ")");
                    return Empty(403, "Forbidden");
            }
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            string url = request?.Url?.ToString() ?? "";
            if (url == RequestPolicy.PageUrl) return false;
            owner._rec.Event("navigation_blocked", RequestPolicy.BlockKey(url));
            return true;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (view is null || url != RequestPolicy.PageUrl) return;
            owner._rec.Mark("page_finished");
            owner.PostChannel(view);
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request is null) return;
            owner._rec.Event("load_error", $"{RequestPolicy.BlockKey(request.Url?.ToString() ?? "")} {error?.ErrorCode} {error?.Description}{(request.IsForMainFrame ? " (main frame)" : "")}");
        }

        public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? response)
        {
            if (request?.Url is not { } u) return;
            // Path only (query strings can carry tokens); e.g. hideme.swf is a known harmless 404 with skua.swf.
            owner._rec.Event("http_error", $"{response?.StatusCode} {u.Scheme}://{u.Host}{u.Path}");
        }

        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            bool crashed = detail?.DidCrash() ?? false;
            int prio = detail?.RendererPriorityAtExit() ?? -1;
            owner._rec.RendererGone(crashed, prio, prio switch { 0 => "waived", 1 => "bound", 2 => "important", _ => "unknown" });
            owner.OnRendererGone(view, crashed);
            return true; // handled: the app keeps running
        }
    }

    private sealed class Chrome(GameViewActivity owner) : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? m)
        {
            if (m is null) return true;
            owner._rec.Console(m.Message() ?? "", m.InvokeMessageLevel()?.Name() ?? "LOG");
            return true; // not written to logcat
        }
    }

    private WebResourceResponse Local(string path)
    {
        Dictionary<string, string> headers = new() { ["Cache-Control"] = "no-store" };
        string mime = RequestPolicy.MimeType(path);
        string? encoding = mime.StartsWith("text/", StringComparison.Ordinal) || mime == "application/json" ? "utf-8" : null;
        if (path == "config.json")
            return new WebResourceResponse(mime, encoding, 200, "OK", headers, new MemoryStream(Encoding.UTF8.GetBytes(_configJson)));
        try
        {
            Stream s = Assets!.Open("web/" + path);
            return new WebResourceResponse(mime, encoding, 200, "OK", headers, s);
        }
        catch (Java.IO.IOException)
        {
            return Empty(404, "Not Found");
        }
    }

    private static WebResourceResponse Empty(int status, string reason) =>
        new("text/plain", "utf-8", status, reason, new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, new MemoryStream());

    private void OnRendererGone(WebView? view, bool crashed)
    {
        _appPort = null;
        // The page is gone: drop its game server connections too (the view cannot be reloaded in the spike).
        _ = _channel?.DisposeAsync().AsTask();
        _ = _loopback?.DisposeAsync().AsTask();
        if (view is not null)
        {
            _root.RemoveView(view);
            view.Destroy();
        }
        if (_web == view) _web = null;
        _status.Visibility = ViewStates.Visible;
        _status.Text = (crashed ? "The WebView renderer crashed." : "The WebView renderer was killed by the system (likely memory).")
            + "\nThe report has it. Tap Report to share, or X to close.";
        SpikeApp.Instance.SaveReport(BuildFinalReport());
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
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    rssMb = double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024;
            Java.Lang.Runtime rt = Java.Lang.Runtime.GetRuntime()!;
            ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
            ActivityManager.MemoryInfo sys = new();
            am.GetMemoryInfo(sys);
            _rec.NativeMemory(new NativeMemorySample(mi.TotalPss / 1024.0, rssMb, (rt.TotalMemory() - rt.FreeMemory()) / 1048576.0,
                Debug.NativeHeapAllocatedSize / 1048576.0, sys.AvailMem / 1048576.0, sys.LowMemory));
        }
        catch (Exception ex)
        {
            _rec.Event("memory_sample_failed", ex.GetType().Name);
        }
    }

    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);
        _rec.Event("trim_memory", level.ToString());
    }

    public override void OnLowMemory()
    {
        base.OnLowMemory();
        _rec.Event("low_memory");
    }

    // ---------------- Lifecycle ----------------

    protected override void OnPause()
    {
        base.OnPause();
        _rec.Event("activity_paused");
        _web?.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _rec.Event("activity_resumed");
        _web?.OnResume();
        GoFullScreen();
    }

    public override void OnBackPressed() => Close("back pressed");

    private void Close(string why)
    {
        if (_closed) { Finish(); return; }
        _rec.Event("closing", why);
        Shutdown();
        Finish();
    }

    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        try { SpikeApp.Instance.SaveReport(BuildFinalReport()); } catch (Exception ex) { SpikeApp.Instance.SaveCrash("saving the report", ex); }
        _memTimer?.Dispose();
        try { _appPort?.Close(); } catch { /* ignore */ }
        _appPort = null;
        if (_web is { } w)
        {
            _root.RemoveView(w);
            w.StopLoading();
            w.Destroy();
            _web = null;
        }
        _ = _channel?.DisposeAsync().AsTask();
        _ = _loopback?.DisposeAsync().AsTask();
        _bridgeThread?.QuitSafely();
        _http.Dispose();
        ClearWebData("view closed");
    }

    /// <summary>The spike keeps nothing the game stores (its Save boxes use localStorage): wiped on open and close.</summary>
    private static void ClearWebData(string when)
    {
        try
        {
            WebStorage.Instance?.DeleteAllData();
            CookieManager.Instance?.RemoveAllCookies(null);
            CookieManager.Instance?.Flush();
        }
        catch { /* best effort */ }
    }

    protected override void OnDestroy()
    {
        Shutdown();
        base.OnDestroy();
    }
}
