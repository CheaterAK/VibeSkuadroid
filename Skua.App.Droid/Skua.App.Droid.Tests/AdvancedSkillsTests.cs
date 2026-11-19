using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Skills;
using Skua.App.Droid.Core.ViewModels;
using Skua.Core.Models.Skills;
using Skua.Core.Skills;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.App.Droid.Tests;

/// <summary>
/// Advanced Skills: Skua's skill-set files (AdvancedSkills.json from the sync, UserAdvancedSkills.json that scripts
/// use) read and written by <see cref="SkillSetStore"/>, the rotation editor (the desktop's skill item and rules
/// ViewModels, compiled in) and the page. Synthetic fixture: Fixtures/AdvancedSkills.sample.json, in the real file's
/// shape. The same checks run over the real community file when SKUA_UPSTREAM_SAMPLES names a folder holding
/// AdvancedSkills.json (fetched from auqw/Scripts, never committed).
/// </summary>
public class AdvancedSkillsTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static SkillSetStore NewStore(string? defaults = null)
    {
        string dir = Sandbox.NewDir();
        if (defaults is not null)
            File.WriteAllText(Path.Combine(dir, SkillSetStore.DefaultsFileName), defaults);
        return new SkillSetStore(dir);
    }

    private static string SampleJson => File.ReadAllText(Fixture("AdvancedSkills.sample.json"));

    /// <summary>The skills as Skua's provider reads them: per skill its id, skip flag and its rules in any order.</summary>
    internal static string Canonical(string skills) => string.Join(" | ", AdvancedSkillsParser.ParseSkillString(skills).Select(s =>
    {
        IEnumerable<string> rules = (s.Rules ?? [])
            .Where(r => r.Type != "None")
            .Select(r => $"{r.Type}:{r.Comparison}:{r.Value}:{r.AuraName}:{r.AuraTarget ?? (r.Type.Contains("Aura") ? "self" : null)}:{r.Timeout}:{r.IsPercentage ?? true}")
            .Order(StringComparer.Ordinal);
        string op = s.MultiAura ? s.MultiAuraOperator ?? "AND" : string.Empty;
        return $"{s.SkillId}{(s.SkipOnMatch ? "S" : string.Empty)}{op}[{string.Join(";", rules)}]";
    }));

    private static Task NoSave(SkillRotationEditorViewModel _) => Task.CompletedTask;

    [Fact]
    public void TheStoreReadsTheCommunityFileAsSkuaDoes()
    {
        SkillSetStore store = NewStore(SampleJson);
        IReadOnlyList<SkillSet> sets = store.Load();
        Assert.Equal(["Sample Gunner/Base", "Sample Gunner/Farm", "Sample Mage/Base", "Sample Mage/Farm", "Sample Tarot Caster/Solo"],
            sets.Select(s => $"{s.ClassName}/{s.Mode}"));
        Assert.All(sets, s => Assert.Equal(SkillSetOrigin.Community, s.Origin));
        SkillSet mage = sets.Single(s => s.ClassName == "Sample Mage" && s.Mode == "Base");
        Assert.Equal("1 A<\"sample burn\" 0 TARGET | 4 H>70% M<70% A<\"Sample Ascension\" 0 | 2 M<30% | 3", mage.Skills);
        Assert.Equal(SkillUseMode.WaitForCooldown, mage.UseMode);
        SkillSet tarot = sets.Single(s => s.ClassName == "Sample Tarot Caster");
        Assert.True(tarot.ResetComboOnTargetChange);
        Assert.Equal(150, tarot.Timeout);
        Assert.Equal("1 MA<\"XXI - The Sample\" 0& MA<\"0 - The Test\" 0 | 4 H>70% MA<\"Mirrored Sample\" 0: MA<\"Arcane Sample\" 0 TARGET | 2 | 3 S", tarot.Skills);
        // No user file yet: reading does not create one (Skua's container does that on its own load).
        Assert.False(File.Exists(store.UserFile));
    }

    [Fact]
    public void EveryRotationOfTheSampleSurvivesTheEditorUnchanged() => AssertEditorRoundTrip(SampleJson);

    /// <summary>The real community file (when available): opening and saving any rotation changes nothing Skua reads.</summary>
    [Fact]
    public void EveryRotationOfTheRealCommunityFileSurvivesTheEditorUnchanged()
    {
        string? dir = Environment.GetEnvironmentVariable("SKUA_UPSTREAM_SAMPLES");
        string file = dir is null ? string.Empty : Path.Combine(dir, "AdvancedSkills.json");
        if (!File.Exists(file))
            return;
        AssertEditorRoundTrip(File.ReadAllText(file), minimum: 200);
    }

    private static void AssertEditorRoundTrip(string communityJson, int minimum = 1)
    {
        SkillSetStore store = NewStore(communityJson);
        store.UpdateFromCommunity();
        IReadOnlyList<SkillSet> before = store.Load();
        Assert.True(before.Count >= minimum, $"{before.Count} rotations");
        StringBuilder problems = new();
        foreach (SkillSet set in before)
        {
            SkillRotationEditorViewModel editor = new(set, null, null, NoSave);
            SkillSet saved = editor.ToSkillSet();
            if (Canonical(saved.Skills) != Canonical(set.Skills))
                problems.AppendLine($"{set.ClassName}/{set.Mode}: {set.Skills}  ->  {saved.Skills}");
            Assert.Equal(set with { Skills = saved.Skills, Origin = SkillSetOrigin.Yours }, saved with { Origin = SkillSetOrigin.Yours });
            if (set.ClassName.Trim().Length == 0)
                continue; // the community file has a rotation for the class "" (kept; the editor cannot save that name)
            store.Save(saved);
        }
        Assert.True(problems.Length == 0, problems.ToString());

        // What Skua loads after saving every rotation through the editor: the same rotations.
        IReadOnlyList<SkillSet> after = store.Load();
        Assert.Equal(before.Count, after.Count);
        foreach ((SkillSet a, SkillSet b) in before.Zip(after))
        {
            Assert.Equal((a.ClassName, a.Mode, a.UseMode, a.Timeout, a.ResetComboOnTargetChange), (b.ClassName, b.Mode, b.UseMode, b.Timeout, b.ResetComboOnTargetChange));
            Assert.Equal(Canonical(a.Skills), Canonical(b.Skills));
        }
        // And the file still parses as Skua's model.
        Assert.NotNull(JsonSerializer.Deserialize<AdvancedSkillsConfigJson>(File.ReadAllText(store.UserFile)));
    }

    [Fact]
    public void EditsAreWrittenOnlyToTheirRotationAndKeepTheRestAsTheyWere()
    {
        SkillSetStore store = NewStore(SampleJson);
        store.UpdateFromCommunity();
        JsonObject before = (JsonObject)JsonNode.Parse(File.ReadAllText(store.UserFile))!;
        SkillSet farm = store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Farm");

        SkillRotationEditorViewModel editor = new(farm, null, null, NoSave);
        editor.MoveUpCommand.Execute(editor.Skills[1]); // 2 before 4
        editor.RemoveCommand.Execute(editor.Skills[2]); // 3
        editor.AddSkillCommand.Execute("5");
        editor.EditRulesCommand.Execute(editor.Skills[^1]);
        SkillRulesEditorViewModel rules = editor.RulesEditor!;
        rules.Health = 40;
        rules.HealthComparison = 0;
        rules.Skip = true;
        Assert.True(rules.UseRule); // setting a condition turns the rules on
        rules.DoneCommand.Execute(null);
        Assert.Null(editor.RulesEditor);
        editor.WaitForCooldown = true;
        editor.Timeout = 400;
        Assert.Equal("2 | 4 | 1 A<\"Sample Blood Boil\" 0S | 5 H<40%S", editor.Preview);
        store.Save(editor.ToSkillSet());

        JsonObject after = (JsonObject)JsonNode.Parse(File.ReadAllText(store.UserFile))!;
        JsonObject mage = (JsonObject)after["Sample Mage"]!;
        Assert.True(JsonNode.DeepEquals(before["Sample Mage"]!["Base"], mage["Base"]));
        Assert.True(JsonNode.DeepEquals(before["Sample Gunner"], after["Sample Gunner"]));
        Assert.True(JsonNode.DeepEquals(before["Sample Tarot Caster"], after["Sample Tarot Caster"]));
        Assert.Equal("WaitForCooldown", (string?)mage["Farm"]!["skillUseMode"]);
        Assert.Equal(400, (int)mage["Farm"]!["skillTimeout"]!);
        JsonArray skills = (JsonArray)mage["Farm"]!["skills"]!;
        Assert.Equal([2, 4, 1, 5], skills.Select(s => (int)s!["skillId"]!));
        Assert.True((bool)skills[3]!["skipOnMatch"]!);
        Assert.Equal("Health", (string?)skills[3]!["rules"]![0]!["type"]);
        Assert.Equal("less", (string?)skills[3]!["rules"]![0]!["comparison"]);

        // Skua's own reader sees the edit.
        AdvancedSkillsConfigJson config = JsonSerializer.Deserialize<AdvancedSkillsConfigJson>(File.ReadAllText(store.UserFile))!;
        Assert.Equal("2 | 4 | 1 A<\"Sample Blood Boil\" 0S | 5 H<40%S", AdvancedSkillsParser.ToSkillString(config["Sample Mage"]["Farm"].Skills));
        Assert.Equal(SkillSetOrigin.Edited, store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Farm").Origin);
    }

    [Fact]
    public void RulesTheDesktopEditorSupportsAreWrittenInItsFormat()
    {
        SkillRotationEditorViewModel editor = new(null, "Sample New", "Solo", NoSave);
        editor.AddSkillCommand.Execute("1");
        editor.AddSkillCommand.Execute("2");
        editor.AddSkillCommand.Execute("3");
        editor.AddSkillCommand.Execute("9"); // not a skill slot
        Assert.Equal(3, editor.Skills.Count);

        editor.EditRulesCommand.Execute(editor.Skills[0]);
        SkillRulesEditorViewModel r = editor.RulesEditor!;
        r.Wait = 300;
        r.Mana = 20;
        r.ManaComparison = 1;
        r.ManaUnit = 1;
        r.PartyHealth = 50;
        Assert.Equal(1, r.PartyHealthComparison); // the desktop's default: above
        r.PartyHealthComparison = 0;
        r.AuraName = "Focus";
        r.AuraStacks = 3;
        r.AuraComparison = 0;
        r.AuraTarget = 1;
        r.DoneCommand.Execute(null);

        editor.EditRulesCommand.Execute(editor.Skills[1]);
        r = editor.RulesEditor!;
        r.AddAuraCheckCommand.Execute(null);
        r.AddAuraCheckCommand.Execute(null);
        Assert.True(r.MultiAura);
        r.AuraChecks[0].AuraName = "Alpha";
        r.AuraChecks[0].StackCount = 2;
        r.AuraChecks[0].IsGreater = true;
        r.AuraChecks[1].AuraName = "Beta";
        r.AuraChecks[1].TargetIndex = 1;
        r.AuraChecks[1].IsGreater = false;
        r.MultiAuraOperator = 1;
        r.DoneCommand.Execute(null);

        // Cancel keeps the skill as it was.
        editor.EditRulesCommand.Execute(editor.Skills[2]);
        editor.RulesEditor!.Health = 10;
        editor.RulesEditor.CancelCommand.Execute(null);

        Assert.Equal("1 WW300 M>20# PH<50% A<\"Focus\" 3 TARGET | 2 MA>\"Alpha\" 2: MA<\"Beta\" 0 TARGET | 3", editor.SkillString());
        List<AdvancedSkillJson> json = AdvancedSkillsParser.ParseSkillString(editor.SkillString());
        Assert.Equal(["Wait", "Mana", "PartyHealth", "Aura"], json[0].Rules!.Select(x => x.Type));
        Assert.False(json[0].Rules![1].IsPercentage);
        Assert.Equal("target", json[0].Rules![3].AuraTarget);
        Assert.True(json[1].MultiAura);
        Assert.Equal("OR", json[1].MultiAuraOperator);
        Assert.Null(json[2].Rules);

        // Clear resets the rules (and does not turn them back on).
        editor.EditRulesCommand.Execute(editor.Skills[0]);
        editor.RulesEditor!.ClearCommand.Execute(null);
        Assert.False(editor.RulesEditor.UseRule);
        editor.RulesEditor.DoneCommand.Execute(null);
        Assert.StartsWith("1 | 2 MA>", editor.SkillString());
    }

    [Fact]
    public void InvalidRotationsAreRefusedAndModesAreSkuas()
    {
        SkillSetStore store = NewStore(SampleJson);
        Assert.Throws<EngineException>(() => store.Save(new SkillSet(" ", "Base", "1", SkillUseMode.UseIfAvailable, 100, false)));
        Assert.Throws<EngineException>(() => store.Save(new SkillSet("X", "Boss", "1", SkillUseMode.UseIfAvailable, 100, false)));
        Assert.Throws<EngineException>(() => store.Save(new SkillSet("X", "3", "1", SkillUseMode.UseIfAvailable, 100, false)));
        Assert.Throws<EngineException>(() => store.Save(new SkillSet("X", "Base", "", SkillUseMode.UseIfAvailable, 100, false)));
        Assert.Equal(Enum.GetNames<ClassUseMode>(), SkillSetStore.Modes);

        // A broken user file is reported, never overwritten silently.
        File.WriteAllText(store.UserFile, "{ not json");
        EngineException e = Assert.Throws<EngineException>(() => store.Save(new SkillSet("X", "Base", "1", SkillUseMode.UseIfAvailable, 100, false)));
        Assert.Contains("UserAdvancedSkills.json", e.Message);
        Assert.Equal("{ not json", File.ReadAllText(store.UserFile));
        store.ResetAll();
        Assert.Equal(5, store.Load().Count);
    }

    /// <summary>
    /// A changed community file never loses the user's work: edited rotations stay, deleted ones stay deleted, the
    /// user's own stay; rotations the user did not touch follow the community file and new ones are added.
    /// </summary>
    [Fact]
    public void CommunityUpdatesKeepTheUsersEdits()
    {
        SkillSetStore store = NewStore(SampleJson);
        Assert.True(store.UpdateFromCommunity()); // first run: the user file is the community file
        Assert.False(store.UpdateFromCommunity()); // nothing new

        SkillSet mageBase = store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Base");
        store.Save(mageBase with { Skills = "4 | 3 | 2 | 1", Timeout = 777 });             // edited
        store.Delete("Sample Gunner", "Base");                                               // deleted
        store.Save(new SkillSet("My Class", "Farm", "1 | 2", SkillUseMode.UseIfAvailable, 100, false)); // own

        JsonObject community = (JsonObject)JsonNode.Parse(SampleJson)!;
        community["Sample Mage"]!["Base"]!["skillTimeout"] = 5;                              // changed, but edited by the user
        community["Sample Gunner"]!["Base"]!["skillTimeout"] = 6;                            // changed, but deleted by the user
        community["Sample Gunner"]!["Farm"]!["skillTimeout"] = 7;                            // changed, untouched
        community["Sample New Class"] = new JsonObject { ["Base"] = JsonNode.Parse("""{"skillUseMode":"UseIfAvailable","skillTimeout":100,"skills":[{"skillId":1}]}""") };
        File.WriteAllText(store.DefaultsFile, community.ToJsonString());

        Assert.True(store.UpdateFromCommunity());
        Dictionary<string, SkillSet> sets = store.Load().ToDictionary(s => $"{s.ClassName}/{s.Mode}");
        Assert.Equal(777, sets["Sample Mage/Base"].Timeout);
        Assert.Equal("4 | 3 | 2 | 1", sets["Sample Mage/Base"].Skills);
        Assert.Equal(SkillSetOrigin.Edited, sets["Sample Mage/Base"].Origin);
        Assert.False(sets.ContainsKey("Sample Gunner/Base"));
        Assert.Equal(7, sets["Sample Gunner/Farm"].Timeout);
        Assert.Equal(SkillSetOrigin.Community, sets["Sample Gunner/Farm"].Origin);
        Assert.Equal(SkillSetOrigin.Yours, sets["My Class/Farm"].Origin);
        Assert.Equal("1", sets["Sample New Class/Base"].Skills);
        Assert.False(store.UpdateFromCommunity());

        // Restoring the community version makes it follow the community file again.
        Assert.True(store.RestoreCommunity("Sample Mage", "Base"));
        Assert.Equal(5, store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Base").Timeout);
        community["Sample Mage"]!["Base"]!["skillTimeout"] = 9;
        File.WriteAllText(store.DefaultsFile, community.ToJsonString());
        Assert.True(store.UpdateFromCommunity());
        Assert.Equal(9, store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Base").Timeout);
    }

    /// <summary>
    /// A user file from before this version (always a copy of an older community file, the app had no editor):
    /// everything in it follows the newer community file, as the old overwrite did.
    /// </summary>
    [Fact]
    public void AUserFileWithoutABaseFollowsTheCommunityFile()
    {
        SkillSetStore store = NewStore(SampleJson);
        File.WriteAllText(store.UserFile, SampleJson);
        JsonObject community = (JsonObject)JsonNode.Parse(SampleJson)!;
        community["Sample Mage"]!["Farm"]!["skillTimeout"] = 321;
        File.WriteAllText(store.DefaultsFile, community.ToJsonString());
        Assert.True(store.UpdateFromCommunity());
        Assert.Equal(321, store.Load().Single(s => s.ClassName == "Sample Mage" && s.Mode == "Farm").Timeout);
        Assert.True(File.Exists(store.BaseFile));
    }

    [Fact]
    public async Task ThePageListsEditsCreatesAndDeletesRotations()
    {
        await using TestApp app = new();
        File.WriteAllText(Path.Combine(app.Paths.SkuaDir, SkillSetStore.DefaultsFileName), SampleJson);
        ToolsViewModel tools = new(app.Services);
        AdvancedSkillsViewModel page = tools.AdvancedSkills;
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Advanced Skills"));
        Assert.Same(page, tools.CurrentPage);
        await Sandbox.Until(() => page.Classes.Count == 3);
        Assert.Equal("3 classes, 5 rotations", page.Summary);

        page.SearchText = "tarot";
        Assert.Equal(["Sample Tarot Caster"], page.Classes.Select(c => c.ClassName));
        page.SearchText = string.Empty;

        // A class and its rotations; edit one.
        page.OpenClassCommand.Execute(page.Classes.Single(c => c.ClassName == "Sample Mage"));
        Assert.True(page.IsClassVisible);
        Assert.Equal("Sample Mage", tools.Title);
        SkillSetRow farm = page.SelectedClass!.Sets.Single(s => s.Mode == "Farm");
        page.EditCommand.Execute(farm);
        SkillRotationEditorViewModel editor = page.Editor!;
        Assert.Equal("Sample Mage (Farm)", tools.Title);
        editor.MoveDownCommand.Execute(editor.Skills[0]);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Error);
        Assert.Null(page.Editor);
        Assert.Equal("2 | 4 | 3 | 1 A<\"Sample Blood Boil\" 0S", page.SelectedClass!.Sets.Single(s => s.Mode == "Farm").Skills);
        Assert.Equal("edited", page.SelectedClass.Sets.Single(s => s.Mode == "Farm").OriginText);
        Assert.True(page.SelectedClass.Sets.Single(s => s.Mode == "Farm").CanRestore);
        Assert.Equal("edited", page.Classes.Single(c => c.ClassName == "Sample Mage").Badge);

        // A new mode for this class (the first mode it has none for), then a duplicate is refused.
        page.NewCommand.Execute(null);
        Assert.Equal("Sample Mage", page.Editor!.ClassName);
        Assert.Equal("Atk", page.Editor.Mode);
        page.Editor.AddSkillCommand.Execute("1");
        page.Editor.Mode = "Base";
        await page.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("already has a Base rotation", page.Editor.Error);
        page.Editor.Mode = "Solo";
        await page.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(page.Editor);
        Assert.Equal(["Base", "Farm", "Solo"], page.SelectedClass!.Sets.Select(s => s.Mode));

        // Back: class -> list; a brand new class from the list joins an existing one ignoring case.
        Assert.True(tools.GoBack());
        Assert.True(page.IsListVisible);
        page.NewCommand.Execute(null);
        page.Editor!.ClassName = "sample gunner";
        page.Editor.Mode = "Dodge";
        page.Editor.AddSkillCommand.Execute("2");
        await page.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Sample Gunner", page.SelectedClass!.ClassName);
        Assert.Contains(page.SelectedClass.Sets, s => s.Mode == "Dodge" && s.OriginText == "yours");

        // Delete asks first.
        SkillSetRow dodge = page.SelectedClass.Sets.Single(s => s.Mode == "Dodge");
        page.DeleteCommand.Execute(dodge);
        Assert.NotNull(page.DeletePrompt);
        await page.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(page.SelectedClass!.Sets, s => s.Mode == "Dodge");

        // Restore and reset.
        page.OpenClassCommand.Execute(page.Classes.Single(c => c.ClassName == "Sample Mage"));
        await page.RestoreCommand.ExecuteAsync(page.SelectedClass!.Sets.Single(s => s.Mode == "Farm"));
        Assert.Equal("community", page.SelectedClass!.Sets.Single(s => s.Mode == "Farm").OriginText);
        page.AskResetCommand.Execute(null);
        Assert.True(page.ConfirmReset);
        await page.ResetAllCommand.ExecuteAsync(null);
        Assert.Equal("3 classes, 5 rotations", page.Summary);

        // System back: class page -> list -> Tools list -> (Settings).
        Assert.True(tools.GoBack());
        Assert.True(tools.GoBack());
        Assert.True(tools.IsListVisible);
        Assert.False(tools.GoBack());
    }
}
