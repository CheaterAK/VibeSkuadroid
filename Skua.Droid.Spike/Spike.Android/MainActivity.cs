using System.Reflection;
using System.Text;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Spike.Core;

namespace Spike.Android;

[Activity(Label = "Skua Spike", MainLauncher = true, ConfigurationChanges =
    global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize | global::Android.Content.PM.ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    // Static so the log survives activity re-creation while a run is in progress.
    static readonly StringBuilder s_log = new();
    static readonly object s_lock = new();
    static volatile bool s_running;
    static MainActivity? s_current;

    TextView? _text;
    ScrollView? _scroll;
    Button? _run;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        s_current = this;

        int pad = (int)(8 * Resources!.DisplayMetrics!.Density);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(pad, pad * 3, pad, pad);

        var title = new TextView(this) { Text = $"VibeSkua runtime-compile spike — {BuildInfo()}" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        root.AddView(title);

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _run = new Button(this) { Text = "Run" };
        _run.Click += (_, _) => StartRun();
        var copy = new Button(this) { Text = "Copy" };
        copy.Click += (_, _) => CopyLog();
        var share = new Button(this) { Text = "Share" };
        share.Click += (_, _) => ShareLog();
        foreach (var b in new[] { _run, copy, share })
            buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(buttons);

        _scroll = new ScrollView(this);
        _text = new TextView(this) { Typeface = Typeface.Monospace };
        _text.SetTextIsSelectable(true);
        _text.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10);
        _scroll.AddView(_text);
        root.AddView(_scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
        lock (s_lock) _text.Text = s_log.Length > 0 ? s_log.ToString() : "Tap Run. The full run takes about 1-3 minutes. Then tap Share (or Copy) and send the text back.\n";
        _run.Enabled = !s_running;
    }

    protected override void OnDestroy()
    {
        if (s_current == this) s_current = null;
        base.OnDestroy();
    }

    static string BuildInfo()
    {
        var attrs = typeof(MainActivity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        string Get(string k) => attrs.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : "-";
        return $"{Get("Configuration")}{(Get("SpikeVariant") != "-" ? "/" + Get("SpikeVariant") : "")}";
    }

    void StartRun()
    {
        if (s_running) return;
        s_running = true;
        _run!.Enabled = false;
        lock (s_lock) s_log.Clear();
        _text!.Text = "";
        int uiThread = Environment.CurrentManagedThreadId;
        var platform = new AndroidPlatform(ApplicationContext!);
        var thread = new Thread(() =>
        {
            try
            {
                new SpikeRunner(platform, Append, new SpikeOptions { UiThreadId = uiThread }).RunAll();
            }
            catch (Exception ex)
            {
                Append("RUNNER CRASHED: " + ex);
            }
            finally
            {
                s_running = false;
                platform.SaveLog(Snapshot());
                s_current?.RunOnUiThread(() => { if (s_current?._run != null) s_current._run.Enabled = true; });
            }
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "spike-runner" };
        thread.Start();
    }

    static string Snapshot()
    {
        lock (s_lock) return s_log.ToString();
    }

    static void Append(string line)
    {
        lock (s_lock) s_log.Append(line).Append('\n');
        global::Android.Util.Log.Info("SkuaSpike", line);
        var a = s_current;
        a?.RunOnUiThread(() =>
        {
            if (a._text == null) return;
            a._text.Append(line + "\n");
            a._scroll?.Post(() => a._scroll.FullScroll(FocusSearchDirection.Down));
        });
    }

    void CopyLog()
    {
        var cm = (ClipboardManager?)GetSystemService(ClipboardService);
        if (cm != null) cm.PrimaryClip = ClipData.NewPlainText("Skua spike log", Snapshot());
        Toast.MakeText(this, "Log copied to clipboard", ToastLength.Short)?.Show();
    }

    void ShareLog()
    {
        var send = new Intent(Intent.ActionSend);
        send.SetType("text/plain");
        send.PutExtra(Intent.ExtraSubject, "VibeSkua Android spike log");
        send.PutExtra(Intent.ExtraText, Snapshot());
        StartActivity(Intent.CreateChooser(send, "Share spike log"));
    }
}
