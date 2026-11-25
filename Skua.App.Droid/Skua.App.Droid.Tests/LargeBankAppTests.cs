using System.Diagnostics;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

public partial class SessionIntegrationTests
{
    private readonly ITestOutputHelper? _output;

    public SessionIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private readonly record struct Cost(double Ms, long Bytes)
    {
        public override string ToString() => $"{Ms,8:0.00} ms {Bytes / 1048576.0,8:0.00} MB";
    }

    private static Cost Measure(Action action, int repeat = 5)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < repeat; i++)
            action();
        sw.Stop();
        return new Cost(sw.Elapsed.TotalMilliseconds / repeat, (GC.GetTotalAllocatedBytes(precise: true) - before) / repeat);
    }

    /// <summary>
    /// A 3000-item bank (the measured account) on the app side: what one World-lists read and one Control-page bank
    /// read cost while the bank is unchanged (they run after every world change and every 400 ms), against the same
    /// reads rebuilding the rows each time (the code before the row caches).
    /// </summary>
    [Fact]
    public async Task ALargeBankCostsTheWorldAndControlPagesNothingWhileItIsUnchanged()
    {
        await using FakeAqw aqw = new();
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
            o.Game = new HeadlessGameOptions { Session = aqw.NewSession().Options, Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(50) } };
        });
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        IControlBackend control = Assert.IsAssignableFrom<IControlBackend>(engine.Plugins?.Control);
        try
        {
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            List<JsonObject> items = LargeBank.Items(3000);
            aqw.Server.LastSession!.Bank.AddRange(items);
            await engine.LoadBankAsync();
            Assert.Equal(3001, engine.GetWorldLists().Bank.Count);
            Assert.Equal(3001, control.ReadBank().Bank.Count);

            Cost lists = Measure(() => engine.GetWorldLists());
            Cost listsRebuilt = Measure(() =>
            {
                engine.TrimCaches();
                engine.GetWorldLists();
            });
            Cost bank = Measure(() => control.ReadBank());
            Assert.Same(control.ReadBank().Bank, control.ReadBank().Bank);
            Assert.Same(engine.GetWorldLists().Bank, engine.GetWorldLists().Bank);

            string report = string.Join('\n',
                "app side, bank of 3001 items:",
                $"  GetWorldLists, bank unchanged:           {lists}",
                $"  GetWorldLists, rows rebuilt (old code):  {listsRebuilt}",
                $"  Control ReadBank, bank unchanged:        {bank}");
            _output?.WriteLine(report);
            if (Environment.GetEnvironmentVariable("SKUA_BANK_REPORT") is string file && file.Length > 0)
                File.AppendAllText(file, report + "\n");
            Assert.True(lists.Bytes < 512 * 1024, $"GetWorldLists allocated {lists}");
            Assert.True(bank.Bytes < 512 * 1024, $"ReadBank allocated {bank}");

            // A change to the bank is seen by the next read.
            Assert.Equal("Moved Pixie Wing to the backpack.", await Task.Run(() => control.BankToInventory(11)));
            await Sandbox.Until(() => control.ReadBank().Bank.Count == 3000);
            Assert.Equal(3000, engine.GetWorldLists().Bank.Count);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
