using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using RuffleSpike.Core;

namespace RuffleSpike.Android;

/// <summary>Settings, the button that opens the game view, and the last report with Copy and Share.</summary>
[Activity(Label = "Game View Spike", MainLauncher = true, Exported = true,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize | global::Android.Content.PM.ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private SpikeSettings _settings = new();
    private TextView? _report;
    private string _reportText = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = SpikeApp.Instance.LoadSettings();
        float dp = Resources!.DisplayMetrics!.Density;
        int pad = (int)(12 * dp);

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        col.SetPadding(pad, pad * 3, pad, pad);

        TextView title = new(this) { Text = "Game view spike (Ruffle in a WebView)" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 18);
        col.AddView(title);
        string webView = WebView.CurrentWebViewPackage is { } p ? $"{p.PackageName} {p.VersionName}" : "unknown";
        string android = global::Android.OS.Build.VERSION.Release ?? "?", model = global::Android.OS.Build.Model ?? "?";
        col.AddView(Small($"Build {Config()} | WebView {webView} | Android {android} | {model}"));
        col.AddView(Small(
            "Opens the real game. Log in on the game's own login screen (do NOT tick its Save boxes). "
            + "This account must not be logged in anywhere else (a second login kicks the first). "
            + "The app keeps no credentials and clears the WebView's storage and cookies when the view opens and closes. "
            + "Close the view with the X button; the report is saved and shown below."));

        Spinner("Loader", SpikeSettings.Swfs, _settings.Swf, v => _settings.Swf = v, col,
            "skua = this repo's skua.swf (modules, measurements); official = the site's Loader4.swf");
        Spinner("Ruffle build", SpikeSettings.RuffleBuilds, _settings.Ruffle, v => _settings.Ruffle = v, col,
            "stable = 0.7.1, nightly = 0.8.0-nightly.2026.11.19");
        Spinner("Frame rate", SpikeSettings.FrameRates.Select(f => f.ToString(CultureInfo.InvariantCulture)).ToArray(),
            _settings.FrameRate.ToString(CultureInfo.InvariantCulture), v => _settings.FrameRate = int.Parse(v, CultureInfo.InvariantCulture), col, null);
        Spinner("Quality", SpikeSettings.Qualities, _settings.Quality, v => _settings.Quality = v, col, null);
        Spinner("Renderer", SpikeSettings.Renderers, _settings.Renderer, v => _settings.Renderer = v, col,
            "auto = Ruffle picks (webgpu, wgpu-webgl, webgl, canvas)");
        Spinner("Socket transport", SpikeSettings.Transports, _settings.Transport, v => _settings.Transport = v, col,
            "channel = private message port to the app (default); loopback = WebSocket on 127.0.0.1 (fallback)");
        Spinner("Max script time (s)", SpikeSettings.MaxExecutionDurations.Select(f => f.ToString(CultureInfo.InvariantCulture)).ToArray(),
            _settings.MaxExecutionDuration.ToString(CultureInfo.InvariantCulture), v => _settings.MaxExecutionDuration = int.Parse(v, CultureInfo.InvariantCulture), col, null);

        Switch("Hide other players (skua module)", _settings.HidePlayers, v => _settings.HidePlayers = v, col);
        Switch("Disable FX (skua module)", _settings.DisableFX, v => _settings.DisableFX = v, col);
        Switch("Kill lag: hide the whole world, UI only (skua)", _settings.KillLag, v => _settings.KillLag = v, col);
        Switch("Skip cutscenes (skua)", _settings.SkipCutscenes, v => _settings.SkipCutscenes = v, col);
        Switch("Mute", _settings.Mute, v => _settings.Mute = v, col);
        Switch("10-minute soak (records 10 min after reaching the game)", _settings.SoakMinutes > 0, v => _settings.SoakMinutes = v ? 10 : 0, col);
        Switch("Show HUD (fps, memory)", _settings.ShowHud, v => _settings.ShowHud = v, col);
        Switch("Allow mixed content (only if loopback fails)", _settings.AllowMixedContent, v => _settings.AllowMixedContent = v, col);

        Button open = new(this) { Text = "Open game view" };
        open.Click += (_, _) =>
        {
            SpikeApp.Instance.SaveSettings(_settings.Normalized());
            StartActivity(new Intent(this, typeof(GameViewActivity)));
        };
        col.AddView(open);

        LinearLayout buttons = new(this) { Orientation = Orientation.Horizontal };
        Button copy = new(this) { Text = "Copy report" };
        copy.Click += (_, _) => Copy(this, _reportText);
        Button share = new(this) { Text = "Share report" };
        share.Click += (_, _) => Share(this, _reportText);
        buttons.AddView(copy, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(share, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        col.AddView(buttons);

        _report = new TextView(this) { Typeface = Typeface.Monospace };
        _report.SetTextIsSelectable(true);
        _report.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10);
        col.AddView(_report);

        ScrollView scroll = new(this);
        scroll.AddView(col);
        SetContentView(scroll);
    }

    protected override void OnResume()
    {
        base.OnResume();
        string? crash = SpikeApp.Instance.TakeCrash();
        string? report = SpikeApp.Instance.LatestReport();
        _reportText = (crash is null ? "" : "APP CRASH IN THE PREVIOUS RUN:\n" + crash + "\n")
            + (report ?? "No report yet. Open the game view, then close it (or tap Report in it).")
            + "\n\nRecent exits of this app (Android's records):\n" + SpikeApp.Instance.RecentExits();
        _report!.Text = _reportText;
    }

    private static string Config()
    {
        var a = typeof(MainActivity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "Configuration");
        return a?.Value ?? "?";
    }

    private TextView Small(string text)
    {
        TextView t = new(this) { Text = text };
        t.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        t.SetPadding(0, 8, 0, 8);
        return t;
    }

    private void Spinner(string label, string[] values, string current, Action<string> set, LinearLayout parent, string? hint)
    {
        LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
        TextView l = new(this) { Text = label };
        row.AddView(l, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Spinner s = new(this);
        s.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, values);
        s.SetSelection(Math.Max(0, Array.IndexOf(values, current)));
        s.ItemSelected += (_, e) => { set(values[e.Position]); SpikeApp.Instance.SaveSettings(_settings.Normalized()); };
        row.AddView(s, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        parent.AddView(row);
        if (hint is not null) parent.AddView(Small(hint));
    }

    private void Switch(string label, bool current, Action<bool> set, LinearLayout parent)
    {
        global::Android.Widget.Switch s = new(this) { Text = label, Checked = current };
        s.CheckedChange += (_, e) => { set(e.IsChecked); SpikeApp.Instance.SaveSettings(_settings.Normalized()); };
        parent.AddView(s);
    }

    public static void Copy(Context context, string text)
    {
        ClipboardManager cm = (ClipboardManager)context.GetSystemService(ClipboardService)!;
        cm.PrimaryClip = ClipData.NewPlainText("game view spike report", text);
        Toast.MakeText(context, "Report copied", ToastLength.Short)!.Show();
    }

    public static void Share(Context context, string text)
    {
        Intent send = new(Intent.ActionSend);
        send.SetType("text/plain");
        send.PutExtra(Intent.ExtraSubject, "Game view spike report");
        send.PutExtra(Intent.ExtraText, text);
        context.StartActivity(Intent.CreateChooser(send, "Share report"));
    }
}
