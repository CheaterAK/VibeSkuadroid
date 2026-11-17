namespace Skua.App.Droid.Core.Platform;

/// <summary>What the ViewModels need from the OS. Android implements it in the app head; tests and the desktop preview use <see cref="NullPlatformServices"/>.</summary>
public interface IPlatformServices
{
    /// <summary>"Android 13 (API 33), Samsung SM-G998B" or similar.</summary>
    string DeviceDescription { get; }

    string AppVersion { get; }

    /// <summary>The OS lets this app post notifications (POST_NOTIFICATIONS on Android 13+).</summary>
    bool NotificationsAllowed { get; }

    /// <summary>Shows the system notification permission prompt (or the app's settings page if it was denied for good).</summary>
    void RequestNotificationPermission();

    /// <summary>The app is exempt from battery optimisation (Doze will not cut its network or ignore its wake lock).</summary>
    bool IgnoringBatteryOptimizations { get; }

    /// <summary>Asks the user to exempt the app from battery optimisation.</summary>
    void RequestIgnoreBatteryOptimizations();

    /// <summary>The engine can be restarted in a fresh process.</summary>
    bool CanRestartProcess { get; }

    /// <summary>
    /// Stops the engine cleanly and restarts the whole app process (the only way to return memory held by script
    /// assemblies on Mono). Logs back in afterwards when <paramref name="resumeLogin"/> and a password is remembered.
    /// </summary>
    void RestartProcess(string reason, bool resumeLogin);

    /// <summary>Opens the system share sheet with plain text.</summary>
    void ShareText(string subject, string text);

    /// <summary>The engine is running inside the foreground service (it survives the UI going away).</summary>
    bool EngineServiceRunning { get; }
}

/// <summary>Platform services for tests and the desktop preview host: nothing is supported.</summary>
public sealed class NullPlatformServices : IPlatformServices
{
    public string DeviceDescription => $"{Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})";

    public string AppVersion => typeof(NullPlatformServices).Assembly.GetName().Version?.ToString() ?? "0";

    public bool NotificationsAllowed => true;

    public void RequestNotificationPermission()
    {
    }

    public bool IgnoringBatteryOptimizations => true;

    public void RequestIgnoreBatteryOptimizations()
    {
    }

    public bool CanRestartProcess => false;

    public void RestartProcess(string reason, bool resumeLogin)
    {
    }

    public string? LastSharedText { get; private set; }

    public void ShareText(string subject, string text) => LastSharedText = text;

    public bool EngineServiceRunning => false;
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs work inline (tests).</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>A file chosen through the system picker (Storage Access Framework on Android).</summary>
/// <param name="RelativePath">Path relative to the picked folder ("Story/Foo.cs"), or just the file name for single files.</param>
/// <param name="Size">Size in bytes, if the provider reports it.</param>
/// <param name="OpenRead">Opens the content.</param>
public sealed record PickedFile(string RelativePath, long? Size, Func<Task<Stream>> OpenRead);

/// <summary>System file and folder pickers.</summary>
public interface IFilePicker
{
    /// <summary>Lets the user pick script files; empty when cancelled.</summary>
    Task<IReadOnlyList<PickedFile>> PickFilesAsync();

    /// <summary>Lets the user pick a folder; returns every file below it (bounded); empty when cancelled.</summary>
    Task<IReadOnlyList<PickedFile>> PickFolderAsync();

    /// <summary>Copies text to the clipboard.</summary>
    Task CopyTextAsync(string text);
}
