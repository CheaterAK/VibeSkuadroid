namespace Spike.Core;

/// <summary>What differs between the Android app and the Linux console host.</summary>
public interface ISpikePlatform
{
    /// <summary>Short name, e.g. "android" or "linux-console".</summary>
    string Name { get; }

    /// <summary>Platform facts (device model, OS version, ABI...).</summary>
    IEnumerable<KeyValuePair<string, string>> EnvironmentFacts();

    /// <summary>
    /// The framework reference assemblies shipped with the app (Android: APK assets; console: the
    /// same files read from the SDK ref pack). Each entry opens a fresh stream; no file paths are used.
    /// </summary>
    IReadOnlyList<(string Name, Func<Stream> Open)> FrameworkReferenceImages();

    /// <summary>Reference image of Spike.HostApi.dll shipped as a file/asset, or null if not shipped.</summary>
    Stream? OpenHostApiReferenceImage();

    /// <summary>App-private writable directory for the compiled-script disk cache.</summary>
    string CacheDirectory { get; }

    /// <summary>Platform PSS in KiB if the platform has its own API for it (Android Debug.GetPss), else null.</summary>
    long? PlatformPssKb();
}
