using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.ViewModels;
using System.Text.RegularExpressions;

namespace Skua.App.Droid.Tests;

/// <summary>
/// The CoreBots options pages: the file they write is the desktop window's (same keys, order and values; the loadout
/// by the desktop's own ViewModels), CoreBots reads back what was chosen, and lines no page knows are kept. Synthetic
/// fixtures: Fixtures/CBO_Storage.sample.txt, Fixtures/CoreBots.sample.cs.txt. With SKUA_UPSTREAM_SAMPLES naming a
/// folder holding the real CoreBots.cs, every setting it reads is checked as well.
/// </summary>
public class CoreBotsOptionsTests
{
    /// <summary>CoreBots.CBOString: the first line that starts with the key, the text after ": ", empty = not set.</summary>
    internal static string? CoreBotsReads(IReadOnlyList<string> lines, string key)
    {
        string[] values = (lines.FirstOrDefault(x => x.StartsWith(key)) ?? ".: fail").Split(": ");
        if (values.Length < 2)
            return null;
        string output = values[1];
        return output != "fail" && !string.IsNullOrWhiteSpace(output) ? output : null;
    }

    private sealed class Page : IAsyncDisposable
    {
        private Page(TestApp app, ShellViewModel shell, FakeSkillContainer skills)
        {
            App = app;
            Shell = shell;
            Skills = skills;
        }

        public TestApp App { get; }

        public ShellViewModel Shell { get; }

        public FakeSkillContainer Skills { get; }

        public CoreBotsOptionsViewModel Vm => Shell.Settings.Tools.CoreBots;

        public string OptionsDir => Path.Combine(App.Paths.SkuaDir, "options");

        public string File => CoreBotsOptions.FileFor(OptionsDir, Vm.Account!);

        public static async Task<Page> OpenAsync(string? existing = null, string coreBots = "CoreBots.sample.cs.txt")
        {
            TestApp app = new(scripts: new ToolsScriptHost(FakeCharacter.Services(out FakeSkillContainer skills)));
            System.IO.File.Copy(AdvancedSkillsTests.Fixture(coreBots), Path.Combine(app.Paths.ScriptsDir, CoreBotsOptions.CoreBotsFile));
            ShellViewModel shell = new(app.Services);
            Page page = new(app, shell, skills);
            await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
            await Sandbox.Until(() => page.Vm.Account is not null);
            if (existing is not null)
            {
                Directory.CreateDirectory(page.OptionsDir);
                System.IO.File.Copy(existing, page.File);
            }
            shell.Settings.OpenToolsCommand.Execute(null);
            shell.Settings.Tools.OpenCommand.Execute(shell.Settings.Tools.Entries.Single(e => e.Title == "CoreBots options"));
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            Shell.Dispose();
            await App.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnUnchangedFileIsWrittenBackInTheDesktopsFormatAndCoreBotsReadsTheSameValues()
    {
        await using Page page = await Page.OpenAsync(AdvancedSkillsTests.Fixture("CBO_Storage.sample.txt"));
        CoreBotsOptionsViewModel vm = page.Vm;
        Assert.True(vm.IsReady, vm.Message);
        Assert.Equal(["Solo", "Farm", "Dodge", "Boss"], vm.Slots.Select(s => s.Name));
        CoreBotsSlot solo = vm.Slots[0];
        Assert.Equal("Void Highlord", solo.ClassName);
        Assert.Equal("Solo", solo.Mode);
        Assert.Equal(["Base", "Solo"], solo.ModeChoices);
        Assert.True(solo.UseEquipment);
        Assert.Equal("Sample Helm", solo.Helm);
        Assert.Equal("Sample Old Cape", solo.Cape); // no longer owned: kept, as the desktop does
        // The character's enhanced classes, "[Current]" first, plus a saved class it no longer has.
        Assert.Equal(["[Current]", "ArchPaladin", "Legacy Class", "Void Highlord"], solo.ClassChoices);
        Assert.Contains("Sample Armor", solo.Armors);
        Assert.Contains("Sample Sword", solo.Weapons);
        Assert.Equal("4242", vm.General.Single(o => o.Key == "PrivateRoomNr").TextValue);
        Assert.Equal("500", vm.General.Single(o => o.Key == "ActionDelayNr").TextValue); // from the old "ActionDelay" line
        Assert.Equal(["BankUnenhancedACGear", "SampleExtraNr"], vm.More.Select(o => o.Key));
        Assert.True(vm.More[0].BoolValue);

        vm.SaveCommand.Execute(null);
        Assert.Contains("Saved", vm.Message);
        string[] written = File.ReadAllLines(page.File);
        string[] expected =
        [
            "SoloClassSelect: Void Highlord", "SoloEquipCheck: True", "SoloModeSelect: Solo",
            "FarmClassSelect: Legacy Class", "FarmEquipCheck: False", "FarmModeSelect: Base",
            "DodgeClassSelect: [Current]", "DodgeEquipCheck: False", "DodgeModeSelect: Base",
            "BossClassSelect: ", "BossEquipCheck: False", "BossModeSelect: Base",
            "Helm1Select: Sample Helm", "Armor1Select: ", "Cape1Select: Sample Old Cape", "Weapon1Select: ", "Pet1Select: ", "GroundItem1Select: ",
            "Helm2Select: ", "Armor2Select: ", "Cape2Select: ", "Weapon2Select: ", "Pet2Select: ", "GroundItem2Select: ",
            "Helm3Select: ", "Armor3Select: ", "Cape3Select: ", "Weapon3Select: ", "Pet3Select: ", "GroundItem3Select: ",
            "Helm4Select: ", "Armor4Select: ", "Cape4Select: ", "Weapon4Select: ", "Pet4Select: ", "GroundItem4Select: ",
            "PrivateRooms: True", "PublicDifficult: False", "BankMiscAC: False", "LoggerInChat: True", "MessageBoxCheck: True",
            "RestCheck: False", "DisableAutoEnhance: False", "DisableBestGear: True", "AntiLag: True", "IncognitoMode: True",
            "PrivateRoomNr: 4242", "ActionDelayNr: 500", "ExitCombatNr: 1600", "HuntDelayNr: 100", "QuestTriesNr: 20", "QuestMaxNr: 150",
            "StopLocationSelect: Whitemap",
            "doGoldBoost: True", "doClassBoost: False", "doRepBoost: False", "doExpBoost: False",
            "Nation_SellMemVoucher: True", "Nation_ReturnPolicyDuringSupplies: False", "UltraAlteonForSupplies: False",
            "PvP_SoloPvPBoss: False", "BCO_Story_TestBot: False",
            // Not on the desktop's tabs: a setting CoreBots reads (More), and a line of another Core kept as it was.
            "BankUnenhancedACGear: True",
            "SampleOtherCoreSetting: kept as it is",
        ];
        Assert.Equal(expected, written);

        // What CoreBots reads from it.
        Assert.Equal("Void Highlord", CoreBotsReads(written, "SoloClassSelect"));
        Assert.Equal("Solo", CoreBotsReads(written, "SoloModeSelect"));
        Assert.Equal("500", CoreBotsReads(written, "ActionDelay"));
        Assert.Equal("4242", CoreBotsReads(written, "PrivateRoomNr"));
        Assert.Equal("True", CoreBotsReads(written, "PrivateRooms"));
        Assert.Equal("Sample Helm", CoreBotsReads(written, "Helm1Select"));
        Assert.Null(CoreBotsReads(written, "Armor1Select"));
        Assert.Null(CoreBotsReads(written, "SampleExtraNr"));
    }

    [Fact]
    public async Task ChoicesOnThePagesReachTheFile()
    {
        await using Page page = await Page.OpenAsync();
        CoreBotsOptionsViewModel vm = page.Vm;
        Assert.True(vm.IsReady, vm.Message);
        CoreBotsSlot farm = vm.Slots.Single(s => s.Name == "Farm");
        farm.ClassName = "ArchPaladin";
        Assert.Equal(["Farm"], farm.ModeChoices);
        farm.Mode = "Farm";
        farm.UseEquipment = true;
        farm.Weapon = "Sample Sword";
        farm.Pet = "Sample Pet";
        CoreBotsSlot boss = vm.Slots.Single(s => s.Name == "Boss");
        boss.ClassName = "Void Highlord";
        boss.Mode = "Solo";
        vm.General.Single(o => o.Key == "ActionDelayNr").TextValue = "900";
        vm.General.Single(o => o.Key == "StopLocationSelect").TextValue = "Yulgar";
        vm.Other.Single(g => g.Title == "Boosters").Options.Single(o => o.Key == "doExpBoost").BoolValue = true;
        vm.More.Single(o => o.Key == "SampleExtraNr").TextValue = "12";

        vm.SaveCommand.Execute(null);
        string[] lines = File.ReadAllLines(page.File);
        Assert.Equal(Path.Combine(page.OptionsDir, "CBO_Storage(hero).txt"), page.File);
        Assert.Equal("ArchPaladin", CoreBotsReads(lines, "FarmClassSelect"));
        Assert.Equal("Farm", CoreBotsReads(lines, "FarmModeSelect"));
        Assert.Equal("True", CoreBotsReads(lines, "FarmEquipCheck"));
        Assert.Equal("Sample Sword", CoreBotsReads(lines, "Weapon2Select"));
        Assert.Equal("Sample Pet", CoreBotsReads(lines, "Pet2Select"));
        Assert.Equal("Void Highlord", CoreBotsReads(lines, "BossClassSelect"));
        Assert.Equal("Solo", CoreBotsReads(lines, "BossModeSelect"));
        Assert.Equal("900", CoreBotsReads(lines, "ActionDelay"));
        Assert.Equal("Yulgar", CoreBotsReads(lines, "StopLocationSelect"));
        Assert.Equal("True", CoreBotsReads(lines, "doExpBoost"));
        Assert.Equal("12", CoreBotsReads(lines, "SampleExtraNr"));
        Assert.Null(CoreBotsReads(lines, "SoloClassSelect")); // nothing chosen: CoreBots uses generic

        // A number that is not one is refused; reload reads the file back.
        vm.General.Single(o => o.Key == "QuestMaxNr").TextValue = "many";
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Not saved", vm.Message);
        vm.ReloadCommand.Execute(null);
        Assert.Equal("150", vm.General.Single(o => o.Key == "QuestMaxNr").TextValue);
        Assert.Equal("ArchPaladin", vm.Slots.Single(s => s.Name == "Farm").ClassName);

        // Load character keeps the selections.
        vm.LoadCharacterCommand.Execute(null);
        Assert.Equal("ArchPaladin", vm.Slots.Single(s => s.Name == "Farm").ClassName);
        Assert.Equal("Sample Sword", vm.Slots.Single(s => s.Name == "Farm").Weapon);
    }

    [Fact]
    public async Task PublicRoomsAreSavedOnlyAfterTheWarning()
    {
        await using Page page = await Page.OpenAsync();
        CoreBotsOptionsViewModel vm = page.Vm;
        vm.General.Single(o => o.Key == "PrivateRooms").BoolValue = false;
        vm.SaveCommand.Execute(null);
        Assert.True(vm.ConfirmPublicRooms);
        Assert.False(File.Exists(page.File));
        vm.KeepPrivateRoomsCommand.Execute(null); // the desktop's "No"
        Assert.Equal("True", CoreBotsReads(File.ReadAllLines(page.File), "PrivateRooms"));

        vm.General.Single(o => o.Key == "PrivateRooms").BoolValue = false;
        vm.SaveCommand.Execute(null);
        vm.UsePublicRoomsCommand.Execute(null);
        Assert.Equal("False", CoreBotsReads(File.ReadAllLines(page.File), "PrivateRooms"));
    }

    [Fact]
    public async Task WithoutALoginOrSkuaThePageSaysWhy()
    {
        await using TestApp app = new();
        ToolsViewModel tools = new(app.Services);
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "CoreBots options"));
        Assert.False(tools.CoreBots.IsReady);
        Assert.False(tools.CoreBots.SaveCommand.CanExecute(null));
        Assert.Contains("Log in first", tools.CoreBots.AccountText);
    }

    /// <summary>The option lists are the desktop window's (Skua.App.Avalonia/AppStartup/CoreBots.cs): keys, order, defaults.</summary>
    [Fact]
    public void TheOptionListsMatchTheDesktopWindow()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Skua.App.Avalonia")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "Skua.App.Avalonia", "AppStartup", "CoreBots.cs"));
        List<string> desktop = new();
        foreach (Match m in Regex.Matches(source, """new (?:DisplayOptionItemViewModel<(\w+)>|CBOBoolOptionItemViewModel)\(([^)]*)\)"""))
        {
            string kind = m.Groups[1].Success ? m.Groups[1].Value : "bool";
            List<string> quoted = Regex.Matches(m.Groups[2].Value, "\"([^\"]*)\"").Select(q => q.Groups[1].Value).ToList();
            string? bare = m.Groups[2].Value.Split(',').Select(t => t.Trim()).LastOrDefault(t => t.Length > 0 && !t.StartsWith('"'));
            string tag, value;
            if (kind == "string" && quoted.Count >= 3)
                (tag, value) = (quoted[^2], quoted[^1]);
            else
                (tag, value) = (quoted[^1], bare ?? (kind == "bool" ? "false" : string.Empty));
            desktop.Add($"{tag}={(value == "true" ? "True" : value == "false" ? "False" : value)}");
        }
        List<string> ours = CoreBotsOptionsViewModel.GeneralOptions().Concat(CoreBotsOptionsViewModel.OtherOptions().SelectMany(g => g.Options))
            .Select(o => $"{o.Key}={o.Default}").ToList();
        Assert.Equal(desktop, ours);
    }

    /// <summary>The real CoreBots.cs (when available): every setting it reads is on a page or under More, and comes back from the file.</summary>
    [Fact]
    public async Task EverySettingTheRealCoreBotsReadsCanBeSetAndIsReadBack()
    {
        string? dir = Environment.GetEnvironmentVariable("SKUA_UPSTREAM_SAMPLES");
        string coreBots = dir is null ? string.Empty : Path.Combine(dir, "CoreBots.cs");
        if (!File.Exists(coreBots))
            return;
        await using Page page = await Page.OpenAsync();
        File.Copy(coreBots, Path.Combine(page.App.Paths.ScriptsDir, CoreBotsOptions.CoreBotsFile), overwrite: true);
        CoreBotsOptionsViewModel vm = page.Vm;
        vm.ReloadCommand.Execute(null);
        IReadOnlyList<(string Key, ScriptOptionKind Kind)> keys = CoreBotsOptions.ReadKeys(page.App.Paths.ScriptsDir);
        Assert.True(keys.Count > 40, $"{keys.Count} keys");

        // Set every setting to a recognisable value through the pages.
        foreach (CoreBotsOption o in vm.General.Concat(vm.Other.SelectMany(g => g.Options)).Concat(vm.More))
        {
            if (o.IsToggle)
                o.BoolValue = !bool.Parse(o.Default);
            else if (o.IsNumber)
                o.TextValue = "4321";
            else
                o.TextValue = "SampleMap";
        }
        foreach (CoreBotsSlot slot in vm.Slots)
        {
            slot.ClassName = "Void Highlord";
            slot.Mode = "Solo";
            slot.UseEquipment = true;
            slot.Helm = "Sample Helm";
            slot.Cape = "Sample Cape";
        }
        vm.SaveCommand.Execute(null);
        if (vm.ConfirmPublicRooms)
            vm.UsePublicRoomsCommand.Execute(null);
        string[] lines = File.ReadAllLines(page.File);
        List<string> missing = keys.Where(k => CoreBotsReads(lines, k.Key) is null).Select(k => k.Key).ToList();
        // Settings CoreBots reads that the file leaves at "not set" can only be the empty gear slots.
        Assert.True(missing.All(k => Regex.IsMatch(k, "^(Armor|Weapon|Pet|GroundItem)\\dSelect$")), string.Join(", ", missing));
        Assert.Equal("Void Highlord", CoreBotsReads(lines, "BossClassSelect"));
        Assert.Equal("Solo", CoreBotsReads(lines, "DodgeModeSelect"));
        Assert.Equal("4321", CoreBotsReads(lines, "ActionDelay"));
        Assert.Equal("Sample Cape", CoreBotsReads(lines, "Cape4Select"));
    }
}
