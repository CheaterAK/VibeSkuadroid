using System.Reflection;
using Android.App;
using Android.Content;
using Android.OS;
using Spike.Core;

namespace Spike.Android;

sealed class AndroidPlatform(Context context) : ISpikePlatform
{
    public string Name => "android";

    public IEnumerable<KeyValuePair<string, string>> EnvironmentFacts()
    {
        yield return new("device", $"{Build.Manufacturer} {Build.Model} ({Build.Device}, {Build.Hardware})");
        yield return new("android", $"{Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), security patch {Build.VERSION.SecurityPatch}");
        yield return new("supported ABIs", string.Join(",", Build.SupportedAbis ?? []));
        yield return new("app nativeLibraryDir", context.ApplicationInfo?.NativeLibraryDir ?? "?");
        yield return new("app package", $"{context.PackageName} (debuggable={((context.ApplicationInfo?.Flags ?? 0) & global::Android.Content.PM.ApplicationInfoFlags.Debuggable) != 0})");
        var am = (ActivityManager?)context.GetSystemService(Context.ActivityService);
        if (am != null)
        {
            var mi = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(mi);
            yield return new("device RAM", $"total={mi.TotalMem / 1048576}MB avail={mi.AvailMem / 1048576}MB lowMemory={mi.LowMemory} threshold={mi.Threshold / 1048576}MB");
            yield return new("java heap class", $"memoryClass={am.MemoryClass}MB largeMemoryClass={am.LargeMemoryClass}MB");
        }
        foreach (var a in typeof(AndroidPlatform).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            yield return new("build " + a.Key, a.Value ?? "");
    }

    public IReadOnlyList<(string Name, Func<Stream> Open)> FrameworkReferenceImages()
    {
        var assets = context.Assets ?? throw new InvalidOperationException("no AssetManager");
        var names = assets.List("refs") ?? [];
        return names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, (Func<Stream>)(() => assets.Open("refs/" + n))))
            .ToList();
    }

    public Stream? OpenHostApiReferenceImage() => context.Assets?.Open("refs-app/Spike.HostApi.dll");

    public string CacheDirectory => context.FilesDir!.AbsolutePath;

    public long? PlatformPssKb() => global::Android.OS.Debug.Pss;

    public void SaveLog(string text)
    {
        try
        {
            var dir = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath;
            File.WriteAllText(Path.Combine(dir, "spike-log.txt"), text);
        }
        catch { }
    }
}
