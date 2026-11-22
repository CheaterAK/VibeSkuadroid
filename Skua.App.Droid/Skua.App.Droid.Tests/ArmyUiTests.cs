using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.Tests;

/// <summary>The Army page: opened from the top bar, its account editor, its cards and commands, rendered at phone size.</summary>
public class ArmyUiTests
{
    [AvaloniaFact]
    public async Task TheArmyPageRendersManagesAccountsAndStartsTheArmy()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new();
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 2);
        AppServices services = new(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, rig.Paths)
        {
            Army = rig.Army,
            DeviceMemory = () => rig.Platform.Memory,
        };
        using ShellViewModel shell = new(services);
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(shell.HasArmy);
        shell.NavigateCommand.Execute("Army");
        Render();
        Assert.Equal("Army", shell.Title);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(ArmyView)));
        ArmyViewModel army = shell.Army!;
        Assert.Equal(4, army.Cards.Count);
        Assert.Contains("total", army.MemoryText);

        // Add an account: the password goes to its own vault entry, never into the account list.
        army.AddAccountCommand.Execute(null);
        Assert.True(army.IsEditing);
        army.EditUsername = "newbie";
        army.SaveAccountCommand.Execute(null);
        Assert.Contains("Enter the password", army.EditError);
        army.EditPassword = "s3cret-pass";
        army.EditTags = "farm, alts";
        army.SaveAccountCommand.Execute(null);
        Assert.False(army.IsEditing);
        Assert.Equal(string.Empty, army.EditPassword);
        ArmyAccount added = rig.Accounts.Current.Accounts.Single(a => a.Username == "newbie");
        Assert.Equal(["farm", "alts"], added.TagList);
        Assert.True(rig.Vault.HasPassword(added.Id));
        Assert.DoesNotContain("s3cret-pass", File.ReadAllText(SlotLayout.AccountsFileOf(rig.Paths)));
        Render();
        Assert.Equal(5, army.Cards.Count);
        // Back closes the editor first.
        army.Cards.Single(c => c.Id == added.Id).EditCommand.Execute(null);
        Assert.True(shell.GoBack());
        Assert.False(army.IsEditing);
        army.Cards.Single(c => c.Id == added.Id).EditCommand.Execute(null);
        army.DeleteAccountCommand.Execute(null);
        Assert.False(rig.Vault.HasPassword(added.Id));

        await army.StartAllCommand.ExecuteAsync(null);
        Render();
        Assert.True(rig.Army.Members.All(m => m.IsOnline), army.StatusText);
        Assert.StartsWith("Start All: 4/4 done", army.StatusText);
        army.Refresh();
        Render();
        Assert.All(army.Cards, c => Assert.True(c.IsOnline));
        Assert.Contains(army.Cards, c => c.Kind.StartsWith("full · slot 2", StringComparison.Ordinal));

        army.JumpMap = "testmap";
        army.JumpCell = "r2";
        army.JumpPad = "Left";
        await army.JumpAllCommand.ExecuteAsync(null);
        Assert.StartsWith("Jump all to testmap: 4/4", army.StatusText);
        army.ScriptPath = "Missing.cs";
        await army.LoadScriptToAllCommand.ExecuteAsync(null);
        Assert.Contains("No script", army.StatusText);
        army.Cards.First(c => c.Member.IsLight).MakeMasterCommand.Execute(null);
        foreach (ArmyCardViewModel c in army.Cards.Where(c => c.Role != "master").ToList())
            c.ToggleFollowerCommand.Execute(null);
        Render();
        Assert.StartsWith("Master", army.RosterText);
        await army.SummonAllCommand.ExecuteAsync(null);
        Assert.Contains("done", army.StatusText);
        await army.LogoutAllCommand.ExecuteAsync(null);
        Render();

        shell.NavigateCommand.Execute("Home");
        Assert.False(shell.GoBack());
        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
