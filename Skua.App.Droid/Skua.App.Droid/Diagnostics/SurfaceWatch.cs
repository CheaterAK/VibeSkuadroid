using Android.App;
using Android.Graphics;
using Android.Runtime;
using Android.Views;

namespace Skua.App.Droid.Diagnostics;

/// <summary>
/// Watches the SurfaceView Avalonia renders into: its surface is created and destroyed as the app goes to the
/// foreground and background, which is where render-thread problems show. Registered after Avalonia's own callback,
/// so it runs after it on every transition.
/// </summary>
internal sealed class SurfaceWatch : Java.Lang.Object, ISurfaceHolderCallback
{
    private SurfaceWatch()
    {
    }

    /// <summary>Adds the watcher to the activity's SurfaceView (Avalonia's view is set by base.OnCreate). False if none was found.</summary>
    public static bool Attach(Activity activity)
    {
        SurfaceView? surface = Find(activity.Window?.DecorView);
        if (surface?.Holder is not { } holder)
        {
            CrashCapture.Info("render surface not found; surface transitions are not recorded");
            return false;
        }
        holder.AddCallback(new SurfaceWatch());
        return true;
    }

    private static SurfaceView? Find(View? view)
    {
        if (view is SurfaceView surface)
            return surface;
        if (view is ViewGroup group)
        {
            for (int i = 0; i < group.ChildCount; i++)
            {
                if (Find(group.GetChildAt(i)) is SurfaceView found)
                    return found;
            }
        }
        return null;
    }

    public void SurfaceCreated(ISurfaceHolder holder) => CrashCapture.Lifecycle("surfaceCreated");

    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Format format, int width, int height) =>
        CrashCapture.Lifecycle($"surfaceChanged {width}x{height}");

    /// <summary>
    /// Runs after Avalonia's own surfaceDestroyed: waits for a frame still being drawn into the window before Android
    /// destroys it (<see cref="RenderFence"/>, upstream Avalonia#22728).
    /// </summary>
    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        TimeSpan? waited = null;
        try
        {
            waited = RenderFence.WaitForFrameInFlight();
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("render fence", e);
        }
        CrashCapture.Lifecycle(waited is { } w && w.TotalMilliseconds >= 1
            ? $"surfaceDestroyed (waited {w.TotalMilliseconds:0} ms for the frame in flight)"
            : "surfaceDestroyed");
    }
}
