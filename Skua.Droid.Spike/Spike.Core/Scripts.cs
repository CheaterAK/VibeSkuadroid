using System.Text;

namespace Spike.Core;

/// <summary>Script sources compiled at runtime by the spike steps.</summary>
public static class Scripts
{
    const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Linq;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;

        """;

    /// <summary>Step 2: no host API, only the BCL.</summary>
    public const string Basic = Usings + """
        public class Script
        {
            public string ScriptMain()
            {
                var primes = Enumerable.Range(2, 200)
                    .Where(n => Enumerable.Range(2, Math.Max(0, (int)Math.Sqrt(n) - 1)).All(d => n % d != 0))
                    .ToList();
                var words = new[] { "alpha", "beta", "gamma", "delta" };
                string joined = string.Join(",", words.OrderByDescending(w => w.Length).ThenBy(w => w));
                // BCL surface the app itself never touches: would break if the trimmer removed it.
                string regex = System.Text.RegularExpressions.Regex.Replace("Skua 2.1.5 droid", @"\d+", m => "<" + m.Value + ">");
                string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, int[]> { ["zipped"] = words.Zip(primes, (w, p) => w.Length * p).ToArray() });
                string xml = new System.Xml.Linq.XElement("bot", new System.Xml.Linq.XAttribute("map", "battleon")).ToString();
                return $"Hello from a runtime-compiled script: {primes.Count} primes below 202 (last {primes[^1]}), words={joined}, " +
                       $"regex={regex} json={json} xml={xml} " +
                       $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
            }
        }
        """;

    /// <summary>Step 3: calls the host API and implements a host interface.</summary>
    public const string HostApi = Usings + """
        using Spike.HostApi;

        public class Script : IScriptPlugin
        {
            public string Name => "HostApiScript";

            public void ScriptMain(IScriptHost bot)
            {
                bot.Log($"Hello {bot.PlayerName} on map {bot.MapId}");
                int events = 0;
                bot.HostEvent += (s, e) => { events++; bot.Log("received host event '" + e.Name + "'"); };
                bot.RaiseHostEvent("ping");
                bot.Set("answer", 42);
                bot.Log("answer read back = " + bot.Get("answer", 0));
                int weapons = bot.Inventory.Where(i => i.Category == ItemCategory.Weapon).Sum(i => i.Quantity);
                bot.Log($"weapon quantity in inventory = {weapons}");
                bot.DelayAsync(10).GetAwaiter().GetResult();
                bot.Set("events", events);
                bot.Set("weapons", weapons);
            }

            public int Execute(IScriptHost host, int input) => input * 2 + host.MapId;
        }
        """;

    /// <summary>Step 6: minimal script that does not leave anything rooted in the host.</summary>
    public const string Collectible = Usings + """
        using Spike.HostApi;

        public class Script
        {
            static readonly List<string> s_static = new();   // statics live in the ALC and must go with it

            public void ScriptMain(IScriptHost bot)
            {
                s_static.Add(bot.PlayerName);
                var data = Enumerable.Range(0, 5000).Select(i => new KeyValuePair<int, string>(i, "v" + i)).ToDictionary(k => k.Key, k => k.Value);
                bot.Set("collectible-result", data.Count + s_static.Count);
            }
        }
        """;

    /// <summary>Step 7: long-running loop on a background thread.</summary>
    public const string Loop = Usings + """
        using Spike.HostApi;

        public class Script
        {
            public void ScriptMain(IScriptHost bot)
            {
                int durationMs = bot.Get("durationMs", 30000);
                var sw = Stopwatch.StartNew();
                int iter = 0;
                long acc = 0;
                long lastReport = 0;
                while (!bot.ShouldStop && sw.ElapsedMilliseconds < durationMs)
                {
                    iter++;
                    acc += Enumerable.Range(0, 2000).Select(x => x * iter % 97).Where(x => x % 2 == 0).Sum();
                    var list = new List<string>();
                    for (int i = 0; i < 50; i++) list.Add($"{iter}:{i}");
                    acc += list.Count;
                    bot.Set("iter", iter);
                    if (sw.ElapsedMilliseconds - lastReport >= 5000)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        bot.Log($"loop alive: t={sw.ElapsedMilliseconds}ms iter={iter} thread={Environment.CurrentManagedThreadId} bg={Thread.CurrentThread.IsBackground}");
                    }
                    bot.DelayAsync(20).Wait();
                }
                bot.Set("iterations", iter);
                bot.Set("acc", acc);
                bot.Set("loopThread", Environment.CurrentManagedThreadId);
            }
        }
        """;

    /// <summary>Script entry for the large compile: drives the synthetic CoreBots library.</summary>
    public const string BigScript = Usings + """
        using Spike.HostApi;
        using CoreBotsSim;

        public class Script
        {
            public void ScriptMain(IScriptHost bot)
            {
                var core = new CoreBots(bot);
                int a = core.RunEverything(7);
                int b = core.RunEverythingAsync(7).GetAwaiter().GetResult();
                string checksum = $"{a:X8}-{b:X8}-{core.Report().Length}";
                bot.Set("checksum", checksum);
                bot.Log($"CoreBotsSim: {core.ModuleCount} modules, checksum {checksum}");
            }
        }
        """;

    /// <summary>
    /// Generates a synthetic "CoreBots"-sized library: one tree per module plus a CoreBots aggregator.
    /// The code is non-trivial (records, generics with constraints, LINQ query and method syntax,
    /// async/await, lambdas, local functions, pattern matching, tuples, interpolation).
    /// </summary>
    public static List<(string Path, string Source)> GenerateCoreBotsLibrary(int modules = 8, int questsPerModule = 22)
    {
        var trees = new List<(string, string)>();
        for (int m = 0; m < modules; m++)
            trees.Add(($"CoreBotsSim/Module{m}.cs", GenerateModule(m, questsPerModule)));

        var sb = new StringBuilder(Usings);
        sb.AppendLine("using Spike.HostApi;");
        sb.AppendLine("namespace CoreBotsSim;");
        sb.AppendLine("""
            public interface IModule
            {
                string Name { get; }
                int RunAll(int seed);
                Task<int> RunAllAsync(int seed, CancellationToken ct);
            }

            public static class CoreExtensions
            {
                public static int Mix(this int a, int b) => unchecked(a * 31 + b);
                public static IEnumerable<T> EveryOther<T>(this IEnumerable<T> src)
                {
                    bool take = true;
                    foreach (var x in src) { if (take) yield return x; take = !take; }
                }
                public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
            }

            public sealed class CoreBots
            {
                private readonly IScriptHost _bot;
                private readonly List<IModule> _modules;
                private readonly Dictionary<string, int> _results = new();

                public int ModuleCount => _modules.Count;
            """);
        sb.AppendLine("    public CoreBots(IScriptHost bot)");
        sb.AppendLine("    {");
        sb.AppendLine("        _bot = bot;");
        sb.AppendLine("        _modules = new List<IModule>");
        sb.AppendLine("        {");
        for (int m = 0; m < modules; m++) sb.AppendLine($"            new Module{m}(bot),");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("""

                public int RunEverything(int seed)
                {
                    int acc = 17;
                    foreach (var m in _modules)
                    {
                        int r = m.RunAll(seed);
                        _results[m.Name] = r;
                        acc = acc.Mix(r);
                    }
                    return acc;
                }

                public async Task<int> RunEverythingAsync(int seed)
                {
                    int acc = 17;
                    foreach (var m in _modules)
                        acc = acc.Mix(await m.RunAllAsync(seed, CancellationToken.None).ConfigureAwait(false));
                    return acc;
                }

                public string Report()
                {
                    var sb = new StringBuilder();
                    foreach (var (name, value) in _results.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
                        sb.Append(name).Append('=').Append(value.ToString("X8")).Append(';');
                    return sb.ToString().EveryOther().Count().Let(n => new string('#', n % 50)) + sb;
                }
            """);
        // Extra host-utility methods, like CoreBots' helpers.
        for (int u = 0; u < 40; u++)
        {
            sb.AppendLine($$"""

                    public bool EnsureQuest{{u}}(int questId, string item = "Item{{u}}", int quantity = {{u + 1}})
                    {
                        if (_bot.ShouldStop) return false;
                        var have = _bot.Inventory.Where(i => i.Name == item).Select(i => i.Quantity).DefaultIfEmpty(0).Sum();
                        if (have >= quantity) { _bot.Set($"quest{{u}}:{questId}", true); return true; }
                        var missing = quantity - have;
                        var msg = missing switch { > 10 => "many", > 1 => "some", _ => "one" };
                        _bot.Set($"quest{{u}}:{questId}:missing", $"{msg}:{missing}");
                        return false;
                    }
                """);
        }
        sb.AppendLine("}");
        trees.Insert(0, ("CoreBotsSim/CoreBots.cs", sb.ToString()));
        return trees;
    }

    static string GenerateModule(int m, int quests)
    {
        var sb = new StringBuilder(Usings);
        sb.AppendLine("using Spike.HostApi;");
        sb.AppendLine("namespace CoreBotsSim;");
        sb.AppendLine($$"""

            public enum Kind{{m}} { A, B, C, D }

            public sealed record Item{{m}}(int Id, string Name, double Weight, Kind{{m}} Kind);

            public sealed class Cache{{m}}<TKey, TValue> where TKey : notnull
            {
                private readonly Dictionary<TKey, TValue> _map = new();
                public int Hits { get; private set; }
                public TValue GetOrAdd(TKey key, Func<TValue> factory)
                {
                    if (_map.TryGetValue(key, out var v)) { Hits++; return v; }
                    v = factory();
                    _map[key] = v;
                    return v;
                }
                public IEnumerable<KeyValuePair<TKey, TValue>> Filter(Func<TKey, TValue, bool> pred) =>
                    _map.Where(kv => pred(kv.Key, kv.Value));
            }

            public sealed partial class Module{{m}} : IModule
            {
                private readonly IScriptHost _host;
                private readonly List<Item{{m}}> _items;
                private readonly Cache{{m}}<string, int> _cache = new();

                public string Name => "Module{{m}}";

                public Module{{m}}(IScriptHost host)
                {
                    _host = host;
                    _items = Enumerable.Range(1, 60)
                        .Select(i => new Item{{m}}(i, $"it{{m}}_{i}", i * 0.5 + {{m}}, (Kind{{m}})(i % 4)))
                        .ToList();
                }

                private static string Describe(object o) => o switch
                {
                    int n when n > 1000 => $"big:{n}",
                    int n => $"small:{n}",
                    string s when s.Length > 3 => s.ToUpperInvariant(),
                    string s => s,
                    Item{{m}} it => it.Name,
                    _ => "?"
                };

            """);

        sb.AppendLine("    public int RunAll(int seed)");
        sb.AppendLine("    {");
        sb.AppendLine("        int acc = 0;");
        for (int k = 0; k < quests; k++) sb.AppendLine($"        acc = unchecked(acc * 7 + Quest{k}(seed));");
        sb.AppendLine("        return acc + _cache.Filter((k, v) => v % 2 == 0).Count();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public async Task<int> RunAllAsync(int seed, CancellationToken ct)");
        sb.AppendLine("    {");
        sb.AppendLine("        var tasks = new List<Task<int>>");
        sb.AppendLine("        {");
        for (int k = 0; k < quests; k++) sb.AppendLine($"            Quest{k}Async(seed, ct),");
        sb.AppendLine("        };");
        sb.AppendLine("        int[] results = await Task.WhenAll(tasks).ConfigureAwait(false);");
        sb.AppendLine("        return results.Aggregate(0, (a, b) => unchecked(a * 17 + b));");
        sb.AppendLine("    }");

        for (int k = 0; k < quests; k++)
        {
            sb.AppendLine($$"""

                    public int Quest{{k}}(int seed)
                    {
                        unchecked
                        {
                            var items = _items
                                .Where(i => (i.Id + seed) % {{k % 5 + 2}} != 0)
                                .Select(i => i with { Weight = i.Weight * {{k + 1}} })
                                .ToList();
                            var grouped = from it in items
                                          group it by it.Kind into g
                                          orderby g.Key
                                          select (Kind: g.Key, Count: g.Count(), Total: g.Sum(x => x.Weight));
                            int acc = seed;
                            foreach (var (kind, count, total) in grouped)
                            {
                                acc = acc * 31 + (int)kind * 7 + count + (int)total;
                            }
                            Func<int, int> f = x => (x * {{k + 3}}) ^ (x >> 3);
                            acc = Enumerable.Range(0, {{10 + k % 7}}).Aggregate(acc, (a, x) => a + f(x));
                            acc += Describe(acc % 4 == 0 ? (object)acc : $"q{{k}}-{acc % 100}").Length;
                            int Local(int v) => v <= 1 ? 1 : v * Local(v - 1) % 1000003;
                            acc += Local({{k % 9 + 2}});
                            var heavy = items.OrderByDescending(i => i.Weight).ThenBy(i => i.Name).Take(5).Select(i => i.Id).ToArray();
                            acc += heavy.Length > 0 ? heavy[0] : -1;
                            int captured = acc;
                            acc = _cache.GetOrAdd("q{{k}}:" + seed, () => captured);
                            if (_host.ShouldStop) return -1;
                            return acc;
                        }
                    }

                    public async Task<int> Quest{{k}}Async(int seed, CancellationToken ct)
                    {
                        int total = 0;
                        for (int i = 0; i < 3; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Yield();
                            total = unchecked(total + Quest{{k}}(seed + i));
                        }
                        var tuple = (total, name: Name);
                        return tuple.name.Length + tuple.total;
                    }
                """);
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}
