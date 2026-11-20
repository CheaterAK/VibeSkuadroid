using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.Skills;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Models;
using System.Text.Json.Nodes;

namespace Skua.App.Droid.Tests;

public partial class SessionIntegrationTests
{
    private const string SkillsScript = """
        using Skua.Core.Interfaces;
        using Skua.Core.Models.Skills;

        public class DroidSkillsRun
        {
            public void ScriptMain(IScriptInterface bot)
            {
                foreach (ClassUseMode mode in new[] { ClassUseMode.Base, ClassUseMode.Farm })
                {
                    bot.Skills.LoadAdvanced("Droid Test Class", false, mode);
                    bot.Log($"rotation {mode}: count={bot.Skills.OverrideProvider!.SkillCount} timeout={bot.Skills.SkillTimeout} use={bot.Skills.SkillUseMode}");
                }
            }
        }
        """;

    /// <summary>
    /// A rotation edited on the Advanced Skills page is what a real script gets from unmodified Skua.Core
    /// (Bot.Skills.LoadAdvanced), and a later community update keeps that edit while updating the untouched rotation.
    /// </summary>
    [Fact]
    public async Task AScriptUsesTheRotationEditedOnThePhone()
    {
        await using FakeAqw aqw = new();
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
            o.Game = new HeadlessGameOptions
            {
                Session = aqw.NewSession().Options,
                Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(200) },
                DownloadMapCells = false,
            };
        });
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
            ScriptStopTimeout = TimeSpan.FromSeconds(30),
        }, host);
        List<LogEntry> log = new();
        engine.LogAdded += (_, e) =>
        {
            lock (log)
                log.Add(e);
        };
        bool Logged(string text)
        {
            lock (log)
                return log.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
        }

        // The app's folders, with Skua's data folder being this process's ClientFileSources.SkuaDIR (as on the phone).
        AppPaths paths = AppPaths.UnderRoot(Sandbox.NewDir());
        Directory.CreateDirectory(paths.FilesDir);
        Directory.CreateSymbolicLink(paths.ApplicationDataDir, Path.GetDirectoryName(ClientFileSources.SkuaDIR)!);
        SkillSetStore store = new(ClientFileSources.SkuaDIR);
        JsonObject community = new()
        {
            ["Droid Test Class"] = new JsonObject
            {
                ["Base"] = JsonNode.Parse("""{"skillUseMode":"UseIfAvailable","skillTimeout":100,"skills":[{"skillId":1},{"skillId":2},{"skillId":3},{"skillId":4}]}"""),
                ["Farm"] = JsonNode.Parse("""{"skillUseMode":"UseIfAvailable","skillTimeout":100,"skills":[{"skillId":1},{"skillId":2}]}"""),
            },
        };
        File.WriteAllText(store.DefaultsFile, community.ToJsonString());
        File.Delete(store.UserFile);
        File.Delete(store.BaseFile);

        await using EngineHost app = new(engine, new SettingsStore(paths.SettingsFile), new CredentialVault(new MemorySecretStore()), new ResumeStore(paths.ResumeFile), new RecordingPlatform());
        ScriptLibrary library = new(paths.ScriptsDir);
        AppServices services = new(app, new InlineDispatcher(), new NoPicker(), library, new ScriptRepositorySync(library, paths.SkuaDir, aqw.Http), paths);
        try
        {
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null);

            // Edit the Base rotation on the page: drop skill 1, wait for cooldown, timeout 321.
            ToolsViewModel tools = new();
            AdvancedSkillsViewModel page = SkillToolPages.AddTo(tools, services).AdvancedSkills;
            await page.RefreshAsync();
            Assert.True(page.Classes.Any(c => c.ClassName == "Droid Test Class"), page.Message + " / " + string.Join(", ", page.Classes.Select(c => c.ClassName)) + " / " + File.ReadAllText(store.UserFile));
            page.OpenClassCommand.Execute(page.Classes.Single(c => c.ClassName == "Droid Test Class"));
            page.EditCommand.Execute(page.SelectedSets.Single(s => s.Mode == "Base"));
            SkillRotationEditorViewModel editor = page.Editor!;
            editor.RemoveCommand.Execute(editor.Skills[0]);
            editor.WaitForCooldown = true;
            editor.Timeout = 321;
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.Null(editor.Error);

            string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidSkills");
            Directory.CreateDirectory(folder);
            string script = Path.Combine(folder, "DroidSkillsRun.cs");
            await File.WriteAllTextAsync(script, SkillsScript);

            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => Logged("rotation Farm:"), 60000);
            Assert.True(Logged("rotation Base: count=3 timeout=321 use=WaitForCooldown"), Dump(log));
            Assert.True(Logged("rotation Farm: count=2 timeout=100 use=UseIfAvailable"), Dump(log));
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
            // Skua clears its own running flag a moment after the run ends.
            await Sandbox.Until(() => !host.Skua!.Services.GetRequiredService<Skua.Core.Interfaces.IScriptManager>().ScriptRunning, 30000);

            // The sync brings a new community file: the edit stays, the untouched rotation follows it.
            community["Droid Test Class"]!["Base"]!["skillTimeout"] = 999;
            community["Droid Test Class"]!["Farm"] = JsonNode.Parse("""{"skillUseMode":"WaitForCooldown","skillTimeout":50,"skills":[{"skillId":1},{"skillId":2},{"skillId":3},{"skillId":4},{"skillId":5}]}""");
            File.WriteAllText(store.DefaultsFile, community.ToJsonString());
            lock (log)
                log.Clear();
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => Logged("rotation Farm:"), 60000);
            Assert.True(Logged("rotation Base: count=3 timeout=321 use=WaitForCooldown"), Dump(log));
            Assert.True(Logged("rotation Farm: count=5 timeout=50 use=WaitForCooldown"), Dump(log));
            Assert.True(Logged("your edits are kept"), Dump(log));
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
        }
        finally
        {
            File.Delete(store.UserFile);
            File.Delete(store.BaseFile);
        }
    }
}
