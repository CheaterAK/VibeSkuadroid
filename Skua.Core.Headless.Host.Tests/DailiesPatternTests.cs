using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// The item reads a CoreBots daily script (0AllDailies: CoreBots + CoreDailies) makes, against a large bank and a full
/// backpack: per call (bytes, time), the first read after a bank change, and a simulated daily (checks, unbanking, a
/// farm loop polling <c>CheckInventory</c> every 250 ms, banking the rewards) for allocation and CPU per second.
/// <see cref="CoreBotsCalls"/> makes exactly the Skua calls CoreBots' CheckInventory, Unbank and ToBank make.
/// </summary>
public sealed class DailiesPatternTests : IAsyncLifetime
{
    /// <summary>A full backpack of a long-played account (the bag holds 100-300 items).</summary>
    private const int BackpackItems = 250;

    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-dailies-" + Environment.ProcessId);
    private readonly ITestOutputHelper _output;
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public DailiesPatternTests(ITestOutputHelper output)
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

    private async Task<(List<JsonObject> Bank, List<JsonObject> Backpack, long HeapBeforeBank)> LoginWithLargeBankAsync(int count)
    {
        List<JsonObject> backpack = LargeBank.Items(BackpackItems, firstId: 300000, seed: 7);
        _world.Server.ExtraInventoryItems.AddRange(backpack);
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        List<JsonObject> bank = LargeBank.Items(count);
        FakeAqwServer.Session server = _world.Server.LastSession!;
        server.Bank.Clear();
        server.Bank.AddRange(bank.Select(i => (JsonObject)i.DeepClone()));
        long heapBeforeBank = Heap();
        await _session.Game.Session.LoadBankAsync();
        Assert.Equal(count, _session.Bot.Bank.Items.Count);
        return (bank, backpack, heapBeforeBank);
    }

    // The managed heap after full collections (this process also holds the test's own copies of the bank).
    private static long Heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string NameOf(JsonObject o) => o["sName"]!.GetValue<string>();

    private static bool Movable(JsonObject o) => o["sType"]!.GetValue<string>() is "Item" or "Resource" or "Quest Item" && o["bCoins"]!.GetValue<int>() == 0;

    [Theory]
    [InlineData(3000)]
    [InlineData(5000)]
    public async Task CoreBotsItemChecksOnALargeBankAreCheap(int count)
    {
        (List<JsonObject> bank, List<JsonObject> backpack, long heapBeforeBank) = await LoginWithLargeBankAsync(count);
        IScriptInterface bot = _session.Bot;
        CoreBotsCalls core = new(bot);
        string inBank = NameOf(bank[count / 2]);
        int inBankId = bank[count / 2]["ItemID"]!.GetValue<int>();
        string inBackpack = NameOf(backpack[BackpackItems / 2]);
        const string missing = "No Such Item";

        List<(string Name, LargeBankTests.Cost Cost)> rows =
        [
            ("TempInv.Contains(name, 5)", Measure(() => bot.TempInv.Contains(missing, 5))),
            ("Inventory.Contains(name, 9999)", Measure(() => bot.Inventory.Contains(inBackpack, 9999))),
            ("House.Contains(name)", Measure(() => bot.House.Contains(missing))),
            ("Bank.Contains(name) found", Measure(() => Assert.True(bot.Bank.Contains(inBank)))),
            ("Bank.Contains(name) missing", Measure(() => Assert.False(bot.Bank.Contains(missing)))),
            ("Bank.Contains(id)", Measure(() => Assert.True(bot.Bank.Contains(inBankId)))),
            ("Bank.TryGetItem(name)", Measure(() => Assert.True(bot.Bank.TryGetItem(inBank, out InventoryItem? _)))),
            ("Bank.GetQuantity(id)", Measure(() => bot.Bank.GetQuantity(inBankId))),
            ("Bank.Items", Measure(() => _ = bot.Bank.Items, 5)),
            ("Inventory.Items", Measure(() => _ = bot.Inventory.Items, 5)),
            ("Inventory.UsedSlots", Measure(() => _ = bot.Inventory.UsedSlots)),
            ("Inventory.IsEquipped(name)", Measure(() => bot.Inventory.IsEquipped(inBackpack))),
            ("Quests.EnsureLoad + CanComplete(1000)", Measure(() => { bot.Quests.EnsureLoad(1000); bot.Quests.CanComplete(1000); }, 5)),
            ("Quests.IsInProgress(1000)", Measure(() => bot.Quests.IsInProgress(1000))),
            ("CheckInventory(missing)", Measure(() => Assert.False(core.CheckInventory(missing)))),
            ("CheckInventory(backpack, 9999)", Measure(() => Assert.False(core.CheckInventory(inBackpack, 9999)))),
            ("CheckInventory(bank, toInv: false)", Measure(() => Assert.True(core.CheckInventory(inBank, toInv: false)))),
        ];

        // The first read after a bank change (a move to the backpack and back): what a script banking items pays.
        JsonObject mover = bank.First(Movable);
        string moverName = NameOf(mover);
        Assert.True(bot.Bank.EnsureToInventory(moverName, loadBank: false));
        rows.Add(("first Bank.Contains after a move", Measure(() => Assert.False(bot.Bank.Contains(moverName)), repeat: 1, warm: false)));
        rows.Add(("first Inventory.Contains after a move", Measure(() => Assert.True(bot.Inventory.Contains(moverName)), repeat: 1, warm: false)));
        Assert.True(bot.Inventory.EnsureToBank(moverName));
        Assert.True(await FakeWorld.Until(() => bot.Bank.Contains(moverName)));

        long retained = Heap() - heapBeforeBank;

        string report = $"bank of {count} items, backpack of {BackpackItems + 3}: the bank and Skua's caches retain {retained / 1048576.0:0.0} MB of managed heap (after the load and these reads)\n"
            + string.Join('\n', rows.Select(r => $"  {r.Name,-38} {r.Cost.Ms * 1000,9:0.0} us {r.Cost.Bytes / 1024.0,10:0.0} KB"));
        _output.WriteLine(report);
        Report(report);

        // Regression bounds: lookups that hand nothing to the script read a shared snapshot (before: 0.4 MB per bank
        // lookup at 3000 items, 0.65 MB at 5000; a backpack lookup parsed the backpack each time).
        foreach ((string name, LargeBankTests.Cost cost) in rows.Where(r => r.Name.StartsWith("Bank.Contains", StringComparison.Ordinal)
                     || r.Name.StartsWith("Bank.TryGetItem", StringComparison.Ordinal) || r.Name.StartsWith("Bank.GetQuantity", StringComparison.Ordinal)
                     || r.Name.StartsWith("CheckInventory", StringComparison.Ordinal)))
            Assert.True(cost.Bytes < 16 * 1024, $"{name} allocated {cost.Bytes} bytes per call");
        foreach ((string name, LargeBankTests.Cost cost) in rows.Where(r => r.Name.StartsWith("Inventory.Contains", StringComparison.Ordinal)
                     || r.Name.StartsWith("TempInv", StringComparison.Ordinal) || r.Name.StartsWith("House", StringComparison.Ordinal)))
            Assert.True(cost.Bytes < 16 * 1024, $"{name} allocated {cost.Bytes} bytes per call");
    }

    /// <summary>
    /// A daily as CoreBots runs it, repeated: check a few items (some banked), unbank two, farm (CheckInventory every
    /// 250 ms, as _KillForItem), bank the two again. Prints allocation and CPU per second; the seconds come from
    /// SKUA_DAILIES_SECONDS (default 8).
    /// </summary>
    [Theory]
    [InlineData(3000, false)]
    [InlineData(5000, false)]
    [InlineData(3000, true)]
    public async Task ASimulatedDailyOnALargeBank(int count, bool idle)
    {
        (List<JsonObject> bank, List<JsonObject> backpack, _) = await LoginWithLargeBankAsync(count);
        IScriptInterface bot = _session.Bot;
        CoreBotsCalls core = new(bot);
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("SKUA_DAILIES_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 8;
        List<string> movers = bank.Where(Movable).Take(2).Select(NameOf).ToList();
        string[] checks = [NameOf(bank[10]), NameOf(bank[count - 10]), NameOf(backpack[5]), NameOf(backpack[200]), "Missing One", "Missing Two"];
        string farmed = NameOf(backpack[100]);

        // Warm up (JIT, first parses), then measure.
        if (idle)
        {
            Thread.Sleep(2000);
        }
        else
        {
            core.Unbank(movers.ToArray());
            core.CheckInventory(farmed, 9999);
            core.ToBank(movers.ToArray());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long heapBefore = GC.GetTotalMemory(true);
        int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Stopwatch sw = Stopwatch.StartNew();
        int cycles = 0;
        long reads = 0;
        using AllocationSampler? sampler = Environment.GetEnvironmentVariable("SKUA_ALLOC_SAMPLE") == "1" ? new AllocationSampler() : null;
        while (idle && sw.Elapsed.TotalSeconds < seconds)
            Thread.Sleep(250);
        while (!idle && sw.Elapsed.TotalSeconds < seconds)
        {
            foreach (string c in checks)
                core.CheckInventory(c, toInv: false);
            core.Unbank(movers.ToArray());
            for (int i = 0; i < 12; i++)
            {
                Assert.False(core.CheckInventory(farmed, 9999));
                reads++;
                Thread.Sleep(250);
            }
            core.ToBank(movers.ToArray());
            cycles++;
        }
        sw.Stop();
        double elapsed = sw.Elapsed.TotalSeconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        long heapAfter = GC.GetTotalMemory(true);
        Assert.All(movers, m => Assert.True(bot.Bank.Contains(m)));

        string report = $"simulated daily{(idle ? " (idle baseline: logged in, nothing called)" : string.Empty)}, bank of {count}: {cycles} cycles in {elapsed:0.0} s ({reads} farm checks): "
            + $"{allocated / elapsed / 1048576.0:0.00} MB/s allocated, CPU {cpu / elapsed:0} ms/s, "
            + $"GC {GC.CollectionCount(0) - gcBefore[0]}/{GC.CollectionCount(1) - gcBefore[1]}/{GC.CollectionCount(2) - gcBefore[2]} (gen0/1/2), "
            + $"heap {heapBefore / 1048576.0:0.0} -> {heapAfter / 1048576.0:0.0} MB";
        _output.WriteLine(report);
        Report(report);
        if (sampler is not null)
            Report(sampler.Top(25));
    }

    private const string FarmScript = """
        using System.Diagnostics;
        using System.Threading;
        using Skua.Core.Interfaces;

        public class LargeBankFarm
        {
            public void ScriptMain(IScriptInterface bot)
            {
                // What CoreBots' SetOptions and a farm turn on: options, the drop grabber, skills.
                bot.Options.LagKiller = true;
                bot.Options.RestPackets = true;
                bot.Options.HuntDelay = 0;
                bot.Drops.Add("Pixie Wing");
                bot.Drops.Start();
                bot.Map.Join("testmap");
                bot.Map.Jump("r2", "Left");
                bot.Skills.StartAdvanced("1 | 2 | 3", 150);
                Stopwatch sw = Stopwatch.StartNew();
                int loops = 0;
                while (sw.Elapsed.TotalSeconds < SECONDS && !bot.ShouldExit)
                {
                    // CoreBots' _KillForItem: CheckInventory (temp, backpack, house, bank) every 250 ms, attack when idle.
                    bool done = bot.TempInv.Contains("FARMED", 9999) || bot.Inventory.Contains("FARMED", 9999) || bot.House.Contains("FARMED") || bot.Bank.Contains("FARMED");
                    if (!bot.Player.HasTarget)
                        bot.Combat.Attack("*");
                    Thread.Sleep(250);
                    loops++;
                }
                bot.Skills.Stop();
                bot.Drops.Stop();
                bot.Log("loops=" + loops);
            }
        }
        """;

    /// <summary>
    /// A running Skua script farming with the drop grabber, skills and options on (what CoreBots does around its
    /// CheckInventory loop), against a large bank and a full backpack: allocation and CPU per second of the process,
    /// with the types that allocate most (SKUA_ALLOC_SAMPLE=1). Seconds from SKUA_DAILIES_SECONDS (default 8).
    /// </summary>
    [Fact]
    public async Task AFarmingScriptOnALargeBank()
    {
        (List<JsonObject> bank, List<JsonObject> backpack, _) = await LoginWithLargeBankAsync(3000);
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("SKUA_DAILIES_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out double sec) ? sec : 8;
        string dir = Path.Combine(s_data, "farm");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "LargeBankFarm.cs");
        await File.WriteAllTextAsync(file, FarmScript.Replace("SECONDS", (seconds + 3).ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("FARMED", NameOf(backpack[100])));
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        _session.ScriptError += (_, e) => error = e;
        _session.ScriptRunningChanged += (_, running) =>
        {
            if (!running)
                stopped.TrySetResult();
        };
        Assert.Null(await _session.StartScriptAsync(file));
        await Task.Delay(3000); // compile, join, first kills
        Assert.False(stopped.Task.IsCompleted, error?.ToString());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        int killsBefore = _session.GetStatus().Kills;
        Stopwatch sw = Stopwatch.StartNew();
        using AllocationSampler? sampler = Environment.GetEnvironmentVariable("SKUA_ALLOC_SAMPLE") == "1" ? new AllocationSampler() : null;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        double elapsed = sw.Elapsed.TotalSeconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        int kills = _session.GetStatus().Kills - killsBefore;
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(error is null, error?.ToString());

        string report = $"farming script, bank of 3000, backpack of {BackpackItems + 3}: {kills} kills in {elapsed:0.0} s: "
            + $"{allocated / elapsed / 1048576.0:0.00} MB/s allocated, CPU {cpu / elapsed:0} ms/s, "
            + $"GC {GC.CollectionCount(0) - gcBefore[0]}/{GC.CollectionCount(1) - gcBefore[1]}/{GC.CollectionCount(2) - gcBefore[2]} (gen0/1/2)";
        _output.WriteLine(report);
        Report(report);
        if (sampler is not null)
            Report(sampler.Top(25));
    }

    // This thread's allocations and the mean time per call (warm: JIT and any cache of the current version).
    private static LargeBankTests.Cost Measure(Action action, int repeat = 50, bool warm = true)
    {
        if (warm)
            action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < repeat; i++)
            action();
        sw.Stop();
        return new LargeBankTests.Cost(sw.Elapsed.TotalMilliseconds / repeat, (GC.GetAllocatedBytesForCurrentThread() - before) / repeat);
    }

    private static void Report(string text)
    {
        if (Environment.GetEnvironmentVariable("SKUA_BANK_REPORT") is string file && file.Length > 0)
            File.AppendAllText(file, text + "\n");
    }
}

/// <summary>
/// The Skua calls CoreBots (auqw/Scripts, CoreBots.cs) makes in CheckInventory, Unbank and ToBank, in the same order,
/// without its logging, gear lists and Game4000 house branches.
/// </summary>
internal sealed class CoreBotsCalls(IScriptInterface bot)
{
    public bool CheckInventory(string? item, int quant = 1, bool toInv = true)
    {
        if (item == null)
            return true;
        if (bot.TempInv.Contains(item, quant))
            return true;
        if (bot.Inventory.Contains(item, quant))
            return true;
        if (bot.House.Contains(item))
            return true;
        if (bot.Bank.Contains(item))
        {
            if (toInv)
                Unbank(item);
            if ((toInv && bot.Inventory.GetQuantity(item) >= quant)
                || (!toInv && bot.Bank.TryGetItem(item, out InventoryItem? found) && found != null && found.Quantity >= quant))
                return true;
        }
        return false;
    }

    public void Unbank(params string[] items)
    {
        foreach (string item in items)
        {
            bool inHouse = bot.House.Contains(item);
            bool inInventory = bot.Inventory.Contains(item);
            bool inBank = bot.Bank.Contains(item);
            if (inHouse || inInventory || !inBank)
                continue;
            InventoryItem? categoryItem = bot.Bank.Items?.FirstOrDefault(c => c != null && c.Name.Equals(item, StringComparison.OrdinalIgnoreCase));
            if (categoryItem == null || !bot.Inventory.HasSpaceFor(categoryItem))
                continue;
            bot.Bank.TryGetItem(item, out InventoryItem? _);
            for (int i = 0; i < 20; i++)
            {
                bot.Bank.EnsureToInventory(item);
                if (bot.Inventory.Contains(item))
                    break;
                Thread.Sleep(100);
            }
        }
    }

    public void ToBank(params string[] items)
    {
        foreach (string item in items)
        {
            if (bot.Inventory.IsEquipped(item))
                continue;
            if (bot.Bank.Contains(item))
                continue;
            ItemBase? inventoryItem = bot.Inventory.Items.Concat(bot.House?.Items ?? Enumerable.Empty<InventoryItem>()).FirstOrDefault(x => x?.Name == item);
            if (inventoryItem == null || (!inventoryItem.Coins && bot.Bank.FreeSlots <= 0))
                continue;
            bot.House!.TryGetItem(item, out InventoryItem? _);
            for (int i = 0; i < 5; i++)
            {
                if (bot.Inventory.EnsureToBank(item))
                    break;
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Samples the runtime's allocation ticks (one per ~100 KB allocated) by type and thread, to find what allocates.</summary>
internal sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
{
    private readonly Dictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
            return;
        int typeIndex = e.PayloadNames!.IndexOf("TypeName");
        int amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
        string type = typeIndex >= 0 ? e.Payload[typeIndex]?.ToString() ?? "?" : "?";
        long amount = amountIndex >= 0 ? Convert.ToInt64(e.Payload[amountIndex]) : 100_000;
        string key = type + " @" + (Thread.CurrentThread.Name ?? "pool");
        lock (_bytes)
            _bytes[key] = _bytes.GetValueOrDefault(key) + amount;
    }

    public string Top(int n)
    {
        lock (_bytes)
            return "allocation samples by type @ thread:\n" + string.Join('\n', _bytes.OrderByDescending(p => p.Value).Take(n).Select(p => $"  {p.Value / 1048576.0,8:0.0} MB  {p.Key}"));
    }
}
