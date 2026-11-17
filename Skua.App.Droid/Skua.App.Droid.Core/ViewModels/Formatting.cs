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

    public static double Percent(int value, int max) => max <= 0 ? 0 : Math.Clamp(value * 100.0 / max, 0, 100);
}
