using Android.Content;
using Android.OS;

namespace Skua.App.Droid;

internal static class ProcessInfo
{
    public const string RestartProcessSuffix = ":restart";

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

    public static int MyPid() => Process.MyPid();
}
