using System.Diagnostics;
using System.Reflection;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid;

/// <summary>
/// Workaround for an Avalonia 12.1.x Android bug (AvaloniaUI/Avalonia#22728, fix proposed in PR #22738, not in 12.1.6):
/// when the app goes to the background, <c>InvalidationAwareSurfaceView.surfaceDestroyed</c> releases the native window
/// and only asks the compositor to stop rendering at its next commit; a frame the render thread is already drawing
/// keeps using the EGL surface of the destroyed window after <c>surfaceDestroyed</c> returns, which Android forbids
/// ("BufferQueue has been abandoned" in logcat; some GPU drivers do not survive it). This waits, bounded, for that
/// in-flight frame: the compositor holds its render lock for the whole frame, so taking and releasing that lock in
/// a surface callback registered after Avalonia's (see <see cref="SurfaceWatch"/>) means no frame is drawing into the
/// window when Android destroys it. Frames that start afterwards see no window and fail cleanly (logged by Avalonia).
/// </summary>
/// <remarks>
/// The lock is private (<c>Compositor.Server</c> -> <c>ServerCompositor._lock</c>) and reached by reflection once; if
/// the Avalonia version no longer has it, this does nothing and says so once in the events log. Remove it when the
/// Avalonia version in use contains the upstream fix (the surface callbacks then share a lock with the render loop).
/// </remarks>
internal static class RenderFence
{
    /// <summary>The longest the main thread waits. The reporter of #22728 measured up to 122 ms on a slow device.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(500);

    private static readonly object s_resolveLock = new();
    private static bool s_resolved;
    private static object? s_renderLock;

    /// <summary>Waits until no frame is being rendered (or <see cref="MaxWait"/> passed). Returns the time waited, or null if unavailable.</summary>
    public static TimeSpan? WaitForFrameInFlight()
    {
        object? renderLock = Resolve();
        if (renderLock is null)
            return null;
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            if (renderLock is Lock newLock)
            {
                if (newLock.TryEnter(MaxWait))
                    newLock.Exit();
                else
                    CrashCapture.Info($"render fence: a frame was still rendering after {MaxWait.TotalMilliseconds:0} ms; surface destroyed anyway");
            }
            else if (Monitor.TryEnter(renderLock, MaxWait))
            {
                Monitor.Exit(renderLock);
            }
            else
            {
                CrashCapture.Info($"render fence: a frame was still rendering after {MaxWait.TotalMilliseconds:0} ms; surface destroyed anyway");
            }
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("waiting for the frame in flight", e);
        }
        return watch.Elapsed;
    }

    private static object? Resolve()
    {
        lock (s_resolveLock)
        {
            if (s_resolved)
                return s_renderLock;
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                Type? platform = typeof(Avalonia.Android.AvaloniaView).Assembly.GetType("Avalonia.Android.AndroidPlatform");
                object? compositor = platform?.GetProperty("Compositor", any)?.GetValue(null);
                if (compositor is null)
                    return null; // Avalonia not started yet: try again next time
                s_resolved = true;
                object? server = compositor.GetType().GetProperty("Server", any)?.GetValue(compositor);
                s_renderLock = server?.GetType().GetField("_lock", any)?.GetValue(server);
                if (s_renderLock is null)
                    CrashCapture.Info("render fence unavailable: this Avalonia version has no compositor render lock where expected");
            }
            catch (Exception e)
            {
                s_resolved = true;
                CrashCapture.NonFatal("finding the compositor render lock", e);
            }
            return s_renderLock;
        }
    }
}
