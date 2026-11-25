using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// What a large bank (3000 items, the measured account; 5000 as headroom) costs end to end: the HTTPS reply and its
/// parse, the world-model insert, and Skua's reads through the bridge (<c>Bank.Items</c>, <c>Bank.Contains</c>), which
/// CoreBots makes many times per kill. Each number is the managed allocation and time of one operation. The bounds
/// are regression checks: before the fix one <c>Bank.Contains</c> on 3000 items allocated over 60 MB.
/// </summary>
public sealed class LargeBankTests : IAsyncLifetime
{
    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-bank-" + Environment.ProcessId);
    private readonly ITestOutputHelper _output;
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public LargeBankTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(50) } },
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _world.DisposeAsync();
    }

    internal readonly record struct Cost(double Ms, long Bytes)
    {
        public override string ToString() => $"{Ms,8:0.0} ms {Bytes / 1048576.0,8:0.00} MB";
    }

    internal static Cost Measure(Action action, int repeat = 1)
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

    [Theory]
    [InlineData(3000)]
    [InlineData(5000)]
    public async Task ALargeBankLoadsAndSkuaReadsItCheaply(int count)
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        List<JsonObject> items = LargeBank.Items(count);
        FakeAqwServer.Session server = _world.Server.LastSession!;
        server.Bank.Clear();
        server.Bank.AddRange(items);
        long bodyBytes = System.Text.Encoding.UTF8.GetByteCount(LargeBank.Body(items));

        GC.Collect();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        Cost load = Measure(() => _session.Game.Session.LoadBankAsync().GetAwaiter().GetResult());
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(count, _session.Game.World.GetBank().Count);

        string last = items[^1]["sName"]!.GetValue<string>();
        string first = items[0]["sName"]!.GetValue<string>();
        List<InventoryItem>? read = null;
        Cost bankItems = Measure(() => read = _session.Bot.Bank.Items, 5);
        Assert.Equal(count, read!.Count);
        Cost contains = Measure(() => Assert.True(_session.Bot.Bank.Contains(last)), 5);
        Cost missing = Measure(() => Assert.False(_session.Bot.Bank.Contains("No Such Item")), 5);
        Cost tryGet = Measure(() => Assert.True(_session.Bot.Bank.TryGetItem(first, out InventoryItem? _)), 5);
        Cost raw = Measure(() => ((IFlashUtil)_session.Flash).GetGameObject("world.bankinfo.items"), 5);
        Cost modelRead = Measure(() => _session.Game.World.GetBank(), 5);

        string report = string.Join('\n',
            $"bank of {count} items: reply body {bodyBytes / 1048576.0:0.00} MB, managed heap +{(heapAfter - heapBefore) / 1048576.0:0.0} MB after the load",
            $"  load (HTTPS reply, parse, model insert): {load}",
            $"  Bank.Items:                              {bankItems}",
            $"  Bank.Contains(name) found:               {contains}",
            $"  Bank.Contains(name) missing:             {missing}",
            $"  Bank.TryGetItem(name):                   {tryGet}",
            $"  getGameObject world.bankinfo.items:      {raw}",
            $"  WorldModel.GetBank:                      {modelRead}");
        _output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("SKUA_BANK_REPORT") is string file && file.Length > 0)
            File.AppendAllText(file, report + "\n");

        // Regression bounds (before: 27 MB and 49 MB per Contains at 3000 and 5000 items; 0.7 s and 4 s per load).
        Assert.True(contains.Bytes < count * 400L, $"Bank.Contains allocated {contains}");
        Assert.True(raw.Bytes < 64 * 1024, $"getGameObject of the bank allocated {raw}");
        Assert.True(load.Ms < 3000, $"the load took {load}");
    }

    [Fact]
    public async Task CachedBankReadsAreWhatAFreshParseGivesAndFollowEveryChange()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        IScriptInterface bot = _session.Bot;
        IFlashUtil flash = _session.Flash;
        FakeAqwServer.Session server = _world.Server.LastSession!;
        server.Bank.AddRange(LargeBank.Items(200));
        await _session.Game.Session.LoadBankAsync();

        List<InventoryItem> fresh = Newtonsoft.Json.JsonConvert.DeserializeObject<List<InventoryItem>>(flash.GetGameObject("world.bankinfo.items")!)!;
        List<InventoryItem> a = bot.Bank.Items;
        List<InventoryItem> b = bot.Bank.Items;
        Assert.Equal(201, a.Count);
        Assert.NotSame(a, b);
        Assert.NotSame(a[0], b[0]);
        Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(fresh), Newtonsoft.Json.JsonConvert.SerializeObject(a));
        int parses = _session.Flash.BankParses;

        // A script changing what it got changes nothing for the next read (as with a fresh parse).
        a[0].Quantity = 999;
        a.Clear();
        Assert.NotEqual(999, bot.Bank.Items[0].Quantity);
        Assert.Equal(parses, _session.Flash.BankParses);

        // A move to the backpack is seen at once, and parsed once.
        InventoryItem wing = bot.Bank.Items.Single(i => i.Name == "Pixie Wing");
        Assert.True(bot.Bank.EnsureToInventory("Pixie Wing", loadBank: false));
        Assert.False(bot.Bank.Contains("Pixie Wing"));
        Assert.Equal(200, bot.Bank.Items.Count);
        Assert.Equal(parses + 1, _session.Flash.BankParses);
        Assert.True(bot.Inventory.Contains(wing.ID));

        // Back to the bank: seen again.
        Assert.True(bot.Inventory.EnsureToBank("Pixie Wing"));
        Assert.True(await FakeWorld.Until(() => bot.Bank.Contains("Pixie Wing")));

        // Other types and paths still parse normally.
        Assert.Equal(201, flash.GetGameObject<List<Dictionary<string, object>>>("world.bankinfo.items")!.Count);
        Assert.Equal(201, flash.GetGameObject<int>("world.bankinfo.items.length"));
    }
}
