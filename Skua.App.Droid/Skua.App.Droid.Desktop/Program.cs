using Avalonia;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Scripting;
using Skua.App.Droid.UI;
using Skua.App.Droid.UI.Services;

namespace Skua.App.Droid.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSkua-droid-preview"));
        AppPaths paths = AppPaths.UnderRoot(root);
        paths.EnsureCreated();
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", paths.ApplicationDataDir);

        SettingsStore settings = new(paths.SettingsFile);
        // Scripts compile against the managed assemblies next to the preview (no APK assets on the desktop).
        SkuaScriptHostAdapter scripts = new(null, new ScriptAssemblyStore(), paths.ScriptsDir);
        HeadlessBotEngine engine = new(new HeadlessEngineOptions { AutoReconnect = settings.Current.AutoReconnect }, scripts);
        // Deliberately no secret store on desktop: the desktop CryptoUtils key is derived from machine/user name.
        EngineHost host = new(engine, settings, new CredentialVault(new UnavailableSecretStore()), new ResumeStore(paths.ResumeFile), new NullPlatformServices());
        ScriptLibrary library = new(paths.ScriptsDir);
        // No secret store here either, so no webhook URL can be stored; drop alerts are only listed.
        NotificationHub notifications = new(engine, new NotificationSettingsStore(paths.NotificationSettingsFile),
            new NotificationHistory(paths.NotificationHistoryFile), new RecordingDropAlertSink(),
            new WebhookUrlVault(new UnavailableSecretStore()), new DiscordWebhookSender());
        AppBootstrap.Factory = () => new AppServices(host, new AvaloniaUiDispatcher(), new AvaloniaFilePicker(), library, new ScriptRepositorySync(library, paths.SkuaDir), paths)
        {
            Notifications = notifications,
        };

        try
        {
            return AppBuilder.Configure<Skua.App.Droid.UI.App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            notifications.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }
}
