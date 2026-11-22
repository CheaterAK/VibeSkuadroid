using Android.Content;
using Android.OS;

namespace Skua.App.Droid;

internal static class ProcessInfo
{
    public const string RestartProcessSuffix = ":restart";

    /// <summary>The game view's own process (GameView/GameViewActivity): Ruffle's memory and crashes stay out of the engine's.</summary>
    public const string GameViewProcessSuffix = ":gameview";

    /// <summary>This process's name ("com.vibeskua.droid" or "com.vibeskua.droid:restart").</summary>
    public static string CurrentName()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && Android.App.Application.ProcessName is string name)
            return name;
        try
        {
            return File.ReadAllText("/proc/self/cmdline").TrimEnd('\0');
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public static bool IsRestartProcess(Context context) => CurrentName().EndsWith(RestartProcessSuffix, StringComparison.Ordinal);

    /// <summary>
    /// A process other than the main one (":restart", ":gameview"): no engine, no Avalonia, no startup tracking, no
    /// exit-reason scan there.
    /// </summary>
    public static bool IsAuxiliaryProcess(Context context) => CurrentName().Contains(':', StringComparison.Ordinal);

    public static int MyPid() => Process.MyPid();
}
