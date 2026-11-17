using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Skua.App.Droid.UI;

namespace Skua.App.Droid;

/// <summary>
/// The Android Application. Runs once per process before any activity, service or receiver: sets up the
/// directory layout and the engine (process-wide), then lets Avalonia initialise.
/// </summary>
/// <remarks>
/// The restart trampoline (<see cref="RestartActivity"/>) runs in a separate ":restart" process; there neither the
/// engine nor Avalonia is initialised.
/// </remarks>
[Application]
[Register("com.vibeskua.droid.DroidApplication")]
public sealed class DroidApplication : AvaloniaAndroidApplication<Skua.App.Droid.UI.App>
{
    public DroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        if (ProcessInfo.IsRestartProcess(this))
            return; // Application.onCreate itself does nothing; skipping base also skips Avalonia here.

        // Must run before Avalonia or any Skua type: it fixes HOME (and so ApplicationData) for this process.
        EngineRuntime.Initialize(this);
        AppBootstrap.Factory = EngineRuntime.CreateAppServices;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
