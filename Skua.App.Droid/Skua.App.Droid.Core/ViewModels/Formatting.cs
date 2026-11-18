using Skua.App.Droid.Core.Engine;
using System.Globalization;

namespace Skua.App.Droid.Core.ViewModels;

internal static class Formatting
{
    public static string Memory(MemoryStatus m)
    {
        if (m.RssBytes < 0)
            return "mem n/a";
        string scripts = m.ScriptAssembliesLoaded > 0 ? $" · {m.ScriptAssembliesLoaded} script asm" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{m.RssMb:0} MB{scripts}");
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"Running DoomVault · 12 min" / "Faulted: Compile error: ..." / "Idle".</summary>
    public static string ScriptLine(ScriptStatus s)
    {
        string name = s.ScriptName is string n ? $" {n}" : string.Empty;
        string head = s.State switch
        {
            ScriptRunState.Running => "Running" + name,
            ScriptRunState.Compiling => "Compiling" + name,
            ScriptRunState.Stopping => "Stopping" + name,
            ScriptRunState.Faulted => "Failed" + name,
            _ => "Idle",
        };
        if (s.State == ScriptRunState.Running && s.StartedAt is DateTimeOffset started)
        {
            TimeSpan up = DateTimeOffset.UtcNow - started;
            head += up.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $" · {(int)up.TotalHours} h {up.Minutes} min")
                : string.Create(CultureInfo.InvariantCulture, $" · {Math.Max(0, (int)up.TotalMinutes)} min");
        }
        return s.Message is string m && s.State is ScriptRunState.Faulted or ScriptRunState.Idle && m != "Stopped." ? $"{head}: {m}" : head;
    }

    public static double Percent(int value, int max) => max <= 0 ? 0 : Math.Clamp(value * 100.0 / max, 0, 100);
}
