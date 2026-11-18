using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

public class ViewModelTests
{
    [Fact]
    public async Task ShellSwitchesFromLoginToDashboardAndBack()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        Assert.IsType<LoginViewModel>(shell.CurrentContent);
        Assert.Equal("Login", shell.Title);

        shell.Login.Account = "hero";
        shell.Login.Password = FakeAqw.Password;
        shell.Login.RememberMe = true;
        Assert.True(shell.Login.LoginCommand.CanExecute(null));
        await shell.Login.LoginCommand.ExecuteAsync(null);
        Assert.Null(shell.Login.ErrorText);
        Assert.Equal(string.Empty, shell.Login.Password); // the typed password does not linger in the form

        await Sandbox.Until(() => shell.ShowDashboard);
        Assert.IsType<DashboardViewModel>(shell.CurrentContent);
        await Sandbox.Until(() => shell.Dashboard.CharacterName.Equals("Hero", StringComparison.OrdinalIgnoreCase));
        Assert.True(shell.IsOnline);

        await shell.Dashboard.LogoutCommand.ExecuteAsync(null);
        await Sandbox.Until(() => !shell.ShowDashboard);
        Assert.True(shell.Login.HasStoredPassword);
        Assert.True(shell.Login.LoginCommand.CanExecute(null)); // empty box + remembered password
    }

    [Fact]
    public async Task BackGoesHomeThenLetsTheSystemHandleIt()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        shell.NavigateCommand.Execute("Console");
        Assert.True(shell.IsConsolePage);
        Assert.True(shell.GoBack());
        Assert.True(shell.IsHomePage);
        Assert.False(shell.GoBack());
    }

    [Fact]
    public async Task LoginErrorsAreShown()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        shell.Login.Account = "hero";
        shell.Login.Password = "wrong-password";
        await shell.Login.LoginCommand.ExecuteAsync(null);
        Assert.Contains("Wrong password", shell.Login.ErrorText);
    }

    [Fact]
    public async Task ServerListLoadsIntoThePicker()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        await shell.Login.LoadServersCommand.ExecuteAsync(null);
        Assert.Equal(["Automatic (least full)", "Gold", "Sandbox"], shell.Login.Servers.Select(s => s.Name));
    }

    [Fact]
    public async Task WorldListsFillWhenThePageIsShown()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        shell.NavigateCommand.Execute("World");
        await Sandbox.Until(() => shell.World.Inventory.Count > 0);
        shell.World.Filter = "zzz-no-such-item";
        Assert.Empty(shell.World.Inventory);
        shell.World.SelectTabCommand.Execute("Bank");
        Assert.True(shell.World.IsBankTab);
        await shell.World.LoadBankCommand.ExecuteAsync(null);
        Assert.True(shell.World.BankLoaded);
    }

    [Fact]
    public async Task ConsoleCopiesSharesAndSendsRawPackets()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => shell.Console.LogLines.Count > 0);

        await shell.Console.CopyLogCommand.ExecuteAsync(null);
        Assert.Contains("Online on Sandbox", app.Picker.Copied);
        shell.Console.ShareLogCommand.Execute(null);
        Assert.Contains("test device", app.Platform.Shared);
        Assert.DoesNotContain(FakeAqw.Password, app.Platform.Shared);

        shell.Console.CaptureEnabled = true;
        Assert.True(app.Engine.PacketCaptureEnabled);
        shell.Console.RawPacket = "%xt%zm%moveToCell%1%r2%Left%";
        await shell.Console.SendPacketCommand.ExecuteAsync(null);
        Assert.Equal("Sent.", shell.Console.Message);
        await Sandbox.Until(() => shell.Console.PacketLines.Any(p => p.Direction == PacketDirection.Out));
    }

    [Fact]
    public void ShareTextIsCappedForTheBinderLimit()
    {
        string tail = ConsoleViewModel.Tail(new string('x', ConsoleViewModel.MaxShareChars + 10));
        Assert.StartsWith("...(truncated)", tail);
        Assert.True(tail.Length < ConsoleViewModel.MaxShareChars + 100);
    }

    [Fact]
    public async Task SettingsPersistAndRestartIsRefusedWithoutPlatformSupport()
    {
        await using TestApp app = new();
        app.Platform.CanRestartProcess = false;
        using ShellViewModel shell = new(app.Services);
        shell.Settings.AutoReconnect = false;
        shell.Settings.MemoryThresholdMb = 2048;
        Assert.False(app.Settings.Current.AutoReconnect);
        Assert.False(app.Engine.AutoReconnect);
        Assert.Equal(2048, app.Settings.Current.MemoryThresholdMb);
        shell.Settings.RestartEngineCommand.Execute(null);
        Assert.Contains("only available on Android", shell.Settings.Message);
        Assert.Empty(app.Platform.Restarts);
    }

    [Fact]
    public async Task BannersFollowPlatformState()
    {
        await using TestApp app = new();
        app.Platform.NotificationsAllowed = false;
        app.Platform.IgnoringBatteryOptimizations = false;
        using ShellViewModel shell = new(app.Services);
        Assert.True(shell.ShowNotificationBanner);
        Assert.True(shell.ShowBatteryBanner);
        shell.DismissBatteryBannerCommand.Execute(null);
        Assert.False(shell.ShowBatteryBanner);
        shell.RefreshPlatformState();
        Assert.False(shell.ShowBatteryBanner); // dismissal is remembered
        shell.RequestNotificationsCommand.Execute(null);
        Assert.Equal(1, app.Platform.NotificationRequests);
    }

    [Fact]
    public async Task ScriptsPageBrowsesAndReportsTheMissingHost()
    {
        await using TestApp app = new();
        Directory.CreateDirectory(Path.Combine(app.Paths.ScriptsDir, "Story"));
        File.WriteAllText(Path.Combine(app.Paths.ScriptsDir, "Story", "Quest.cs"), "class Quest {}");
        using ShellViewModel shell = new(app.Services);
        shell.NavigateCommand.Execute("Scripts");
        Assert.True(shell.Scripts.ScriptsUnavailable);
        Assert.Single(shell.Scripts.Entries);
        shell.Scripts.OpenCommand.Execute(shell.Scripts.Entries[0]);
        Assert.Equal("Story", shell.Scripts.CurrentFolder);
        shell.Scripts.OpenCommand.Execute(shell.Scripts.Entries[0]);
        Assert.Equal("Story/Quest.cs", shell.Scripts.LoadedScript);
        await shell.Scripts.StartCommand.ExecuteAsync(null);
        Assert.Contains("no script host", shell.Scripts.Message);
        Assert.True(shell.GoBack()); // up a folder first
        Assert.Equal(string.Empty, shell.Scripts.CurrentFolder);
    }
}
