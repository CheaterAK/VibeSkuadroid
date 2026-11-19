using System.Text;
using Android.Content;
using Android.Runtime;
using RuffleSpike.Core;

namespace RuffleSpike.Android;

/// <summary>Process-wide pieces: crash capture, settings and the saved reports (files/reports, last 20).</summary>
[Application]
public sealed class SpikeApp : Application
{
    public SpikeApp(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public static SpikeApp Instance { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => SaveCrash("Android unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => SaveCrash("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { SaveCrash("unobserved task exception", e.Exception); e.SetObserved(); };
    }

    private string ReportsDir
    {
        get
        {
            string dir = Path.Combine(FilesDir!.AbsolutePath, "reports");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void SaveCrash(string what, Exception? ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(FilesDir!.AbsolutePath, "crash.txt"),
                $"{DateTimeOffset.Now:O} {what}\n{ex}\n");
        }
        catch { /* nothing more to do */ }
    }

    /// <summary>The crash text of the previous run (and removes it), or null.</summary>
    public string? TakeCrash()
    {
        string path = Path.Combine(FilesDir!.AbsolutePath, "crash.txt");
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path);
        File.Delete(path);
        return text;
    }

    public string SaveReport(string text)
    {
        string path = Path.Combine(ReportsDir, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, text, Encoding.UTF8);
        foreach (string old in Directory.GetFiles(ReportsDir, "report-*.txt").OrderDescending().Skip(20))
            File.Delete(old);
        return path;
    }

    public string? LatestReport()
    {
        string? last = Directory.GetFiles(ReportsDir, "report-*.txt").OrderDescending().FirstOrDefault();
        return last is null ? null : File.ReadAllText(last);
    }

    public SpikeSettings LoadSettings() =>
        SpikeSettings.FromJson(GetSharedPreferences("spike", FileCreationMode.Private)!.GetString("settings", null));

    public void SaveSettings(SpikeSettings s)
    {
        ISharedPreferencesEditor e = GetSharedPreferences("spike", FileCreationMode.Private)!.Edit()!;
        e.PutString("settings", s.ToJson());
        e.Apply();
    }

    /// <summary>Android's own records of how this app's recent processes ended (Android 11+).</summary>
    public string RecentExits(int max = 5)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return "(exit records need Android 11+)";
        try
        {
            ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
            IList<ApplicationExitInfo> exits = am.GetHistoricalProcessExitReasons(PackageName, 0, max);
            if (exits.Count == 0) return "(none)";
            StringBuilder sb = new();
            foreach (ApplicationExitInfo e in exits)
                sb.AppendLine($"  {DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).ToLocalTime():yyyy-MM-dd HH:mm:ss} "
                    + $"{ReasonName((int)e.Reason)} status={e.Status} importance={e.Importance} pss={e.Pss / 1024}MB rss={e.Rss / 1024}MB {e.Description}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return "(unavailable: " + ex.GetType().Name + ")"; }
    }

    private static readonly string[] ReasonNames =
    [
        "UNKNOWN", "EXIT_SELF", "SIGNALED", "LOW_MEMORY", "CRASH", "CRASH_NATIVE", "ANR", "INITIALIZATION_FAILURE",
        "PERMISSION_CHANGE", "EXCESSIVE_RESOURCE_USAGE", "USER_REQUESTED", "USER_STOPPED", "DEPENDENCY_DIED", "OTHER",
        "FREEZER", "PACKAGE_STATE_CHANGE", "PACKAGE_UPDATED",
    ];

    private static string ReasonName(int r) => r >= 0 && r < ReasonNames.Length ? ReasonNames[r] : "REASON_" + r;
}
