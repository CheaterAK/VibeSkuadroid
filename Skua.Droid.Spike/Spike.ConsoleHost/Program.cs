using Spike.Core;
using Spike.HostApi;

int bgSeconds = 30;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--bg-seconds") bgSeconds = int.Parse(args[i + 1]);

var platform = new ConsolePlatform();
var runner = new SpikeRunner(platform, Console.WriteLine, new SpikeOptions
{
    BackgroundSeconds = bgSeconds,
    UiThreadId = Environment.CurrentManagedThreadId,
});
// Run on a worker thread, as the Android app does.
var t = new Thread(runner.RunAll) { IsBackground = false, Name = "spike-runner" };
t.Start();
t.Join();

sealed class ConsolePlatform : ISpikePlatform
{
    readonly string _refsDir = Path.Combine(AppContext.BaseDirectory, "refs");

    public string Name => "linux-console";

    public IEnumerable<KeyValuePair<string, string>> EnvironmentFacts()
    {
        yield return new("host", Environment.MachineName);
        string osRelease = File.Exists("/etc/os-release")
            ? File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="))?.Split('=', 2)[1].Trim('"') ?? "?"
            : "?";
        yield return new("os", osRelease);
        yield return new("refs dir", _refsDir);
    }

    public IReadOnlyList<(string Name, Func<Stream> Open)> FrameworkReferenceImages() =>
        Directory.GetFiles(_refsDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), (Func<Stream>)(() => File.OpenRead(f))))
            .ToList();

    public Stream? OpenHostApiReferenceImage()
    {
        string loc = typeof(IScriptHost).Assembly.Location;
        return string.IsNullOrEmpty(loc) ? null : File.OpenRead(loc);
    }

    public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "vibeskua-spike-cache");

    public long? PlatformPssKb() => null;
}
