using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Scripting;
using Skua.Core;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Scripts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Skua.App.Droid.Tests;

/// <summary>
/// Build-time safety net for on-device scripting: compiles real scripts through Skua.Core's compiler using
/// exactly the reference images the APK packs (copied by ScriptReferences.targets into script-refs/).
/// </summary>
public class ScriptReferenceTests
{
    private static readonly string RefsDir = Path.Combine(AppContext.BaseDirectory, "script-refs");

    // Referenced by Skua.Core but deliberately not offered to scripts (with the reason).
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.CodeAnalysis"] = "the compiler itself; ~10 MB of images and no script API uses it",
        ["Microsoft.CodeAnalysis.CSharp"] = "the compiler itself",
        ["Microsoft.CodeAnalysis.Scripting"] = "the compiler itself",
        ["Microsoft.CodeAnalysis.CSharp.Scripting"] = "the compiler itself",
    };

    private static ScriptReferenceSet Load() => ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(RefsDir));

    [Fact]
    public void TheAssetSetIsCompleteAndMatchesItsManifest()
    {
        ScriptReferenceSet set = Load();
        Assert.True(set.IsComplete, string.Join("; ", set.Problems));
        Assert.True(set.Files.Count(f => f.StartsWith("framework/")) >= 150);
        foreach (string required in ScriptReferenceSet.Required)
            Assert.Contains(required, set.Files);
        Assert.All(set.Provider.GetReferences(), r => Assert.DoesNotContain(Path.DirectorySeparatorChar, r.FilePath!));
    }

    [Fact]
    public void EveryAssemblySkuaCoreReachesIsOfferedOrDeliberatelyExcluded()
    {
        HashSet<string> offered = new(Load().Files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new(["Skua.Core", "Skua.Compat.WinForms"]);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> missing = new();
        while (pending.Count > 0)
        {
            string name = pending.Dequeue();
            if (!seen.Add(name) || Excluded.ContainsKey(name))
                continue;
            if (!offered.Contains(name))
            {
                missing.Add(name);
                continue;
            }
            string? path = new[] { Path.Combine(RefsDir, "app", name + ".dll") }.FirstOrDefault(File.Exists);
            if (path is null)
                continue; // framework reference assembly: its own references are framework too
            using PEReader pe = new(File.OpenRead(path));
            MetadataReader md = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
                pending.Enqueue(md.GetString(md.GetAssemblyReference(h).Name));
        }
        Assert.True(missing.Count == 0, "Not in script-refs and not excluded: " + string.Join(", ", missing));
    }

    [Fact]
    public void ApplicationDataLayoutMatchesClientFileSources()
    {
        // In tests ApplicationData comes from XDG_CONFIG_HOME; on Android it is $HOME/.config (AppPaths.ApplicationDataDir).
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, "VibeSkua", "Scripts"), ClientFileSources.SkuaScriptsDIR);
    }

    [Fact]
    public void BasicScriptCompilesAgainstTheAssetSetAndRuns()
    {
        object script = Compile(Scripts.Basic, "DroidBasic");
        ScriptInterfaceStub stub = Run(script);
        Assert.Contains("sum=6", stub.Logs);
        Assert.Contains("json=True", stub.Logs);
    }

    [Fact]
    public void WinFormsStyleScriptCompilesAgainstTheShim()
    {
        object script = Compile(Scripts.WinForms, "DroidWinForms");
        ScriptInterfaceStub stub = Run(script);
        Assert.Contains("answer=No", stub.Logs);
    }

    [Fact]
    public void ScriptUsingSkuaModelsAndOptionsCompiles()
    {
        object script = Compile(Scripts.SkuaApi, "DroidSkuaApi");
        ScriptInterfaceStub stub = Run(script);
        Assert.Contains("ok", stub.Logs);
    }

    private static object Compile(string source, string name)
    {
        ScriptReferenceSet set = Load();
        IServiceProvider services = new ServiceCollection()
            .AddSingleton<IScriptReferenceProvider>(set.Provider)
            .AddSingleton<IScriptNamespaceProvider>(ScriptReferenceSet.Namespaces)
            .BuildServiceProvider();
        Compiler compiler = Skua.Core.AppStartup.Services.CreateCompiler(services);
        object? instance = compiler.CompileClass(source, HashCode.Combine(source, name, Guid.NewGuid()), null, name + Guid.NewGuid().ToString("N")[..6]);
        Assert.False(compiler.Error, compiler.ErrorMessage);
        return instance!;
    }

    private static ScriptInterfaceStub Run(object script)
    {
        IScriptInterface bot = DispatchProxy.Create<IScriptInterface, ScriptInterfaceStub>();
        script.GetType().GetMethod("ScriptMain")!.Invoke(script, [bot]);
        return (ScriptInterfaceStub)(object)bot;
    }
}

public class ScriptAssemblyStoreTests
{
    private static byte[] Emit(string name, string source, params MetadataReference[] extra)
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] refs = Directory.EnumerateFiles(runtimeDir, "*.dll")
            .Where(p => !p.Contains("Native", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Concat(extra)
            .ToArray();
        CSharpCompilation c = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using MemoryStream ms = new();
        var result = c.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return ms.ToArray();
    }

    [Fact]
    public void IdenticalImagesLoadOnceAndChangedOnesSideBySide()
    {
        ScriptAssemblyStore store = new();
        int loads = 0;
        store.Loaded += (_, _) => loads++;
        string suffix = Guid.NewGuid().ToString("N")[..8];
        byte[] v1 = Emit("Inc" + suffix, "public static class Inc { public static int V() => 1; }");
        byte[] v2 = Emit("Inc" + suffix, "public static class Inc { public static int V() => 2; }");

        Assembly a = store.GetOrLoad(v1);
        Assert.Same(a, store.GetOrLoad(v1.ToArray()));
        Assembly b = store.GetOrLoad(v2); // same simple name, different content: separate context
        Assert.NotSame(a, b);
        Assert.Equal(2, store.Count);
        Assert.Equal(2, loads);
        Assert.Equal(1, a.GetType("Inc")!.GetMethod("V")!.Invoke(null, null));
        Assert.Equal(2, b.GetType("Inc")!.GetMethod("V")!.Invoke(null, null));
    }

    [Fact]
    public void ScriptResolvesTheIncludeItWasCompiledAgainst()
    {
        ScriptAssemblyStore store = new();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        byte[] include = Emit("CoreBots" + suffix, "public class CoreBots { public string Hello() => \"hi\"; }");
        Assembly includeAsm = store.GetOrLoad(include);
        byte[] script = Emit("Script" + suffix, "public class S { public string Run() => new CoreBots().Hello(); }",
            MetadataReference.CreateFromImage(include));

        Assembly scriptAsm = store.GetOrLoad(script, new Dictionary<string, Assembly> { [includeAsm.GetName().Name!] = includeAsm });
        object s = Activator.CreateInstance(scriptAsm.GetType("S")!)!;
        Assert.Equal("hi", s.GetType().GetMethod("Run")!.Invoke(s, null));
        Assert.True(store.TryGet(include, out Assembly? again) && ReferenceEquals(again, includeAsm));
    }

    [Fact]
    public void AnUnchangedImageBindsToAChangedDependencyAsANewLoad()
    {
        // CoreFarms recompiled against an edited CoreBots can be byte-identical (references are by name): it must bind
        // to the new CoreBots, not reuse the copy bound to the old one; with the same CoreBots it is reused.
        ScriptAssemblyStore store = new();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        byte[] core1 = Emit("CoreX" + suffix, "public static class CoreX { public static int V() => 1; }");
        byte[] core2 = Emit("CoreX" + suffix, "public static class CoreX { public static int V() => 2; }");
        byte[] farms = Emit("Farms" + suffix, "public static class Farms { public static int V() => CoreX.V() * 10; }", MetadataReference.CreateFromImage(core1));
        Assembly c1 = store.GetOrLoad(core1);
        Assembly c2 = store.GetOrLoad(core2);
        string name = c1.GetName().Name!;

        Assembly f1 = store.GetOrLoad(farms, new Dictionary<string, Assembly> { [name] = c1, ["Unrelated"] = typeof(object).Assembly });
        Assembly f2 = store.GetOrLoad(farms, new Dictionary<string, Assembly> { [name] = c2 });
        Assert.NotSame(f1, f2);
        Assert.Equal(10, f1.GetType("Farms")!.GetMethod("V")!.Invoke(null, null));
        Assert.Equal(20, f2.GetType("Farms")!.GetMethod("V")!.Invoke(null, null));
        Assert.Same(f1, store.GetOrLoad(farms, new Dictionary<string, Assembly> { [name] = c1 })); // unrelated entries do not matter
        Assert.Equal(4, store.Count);
        Assert.IsAssignableFrom<Skua.Core.ScriptLoadContext>(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(f1));
    }
}

public class ScriptInterfaceStub : DispatchProxy
{
    public List<string> Logs { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.Name == "Log" && args is [string message])
        {
            Logs.Add(message);
            return null;
        }
        Type returnType = targetMethod.ReturnType;
        return returnType != typeof(void) && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

internal static class Scripts
{
    public const string Basic = """
        public class DroidBasicScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                List<int> values = new() { 1, 2, 3 };
                bot.Log("sum=" + values.Sum());
                JObject json = JObject.Parse("{\"ok\":true}");
                bot.Log("json=" + (bool)json["ok"]!);
                bot.Log("re=" + Regex.IsMatch("abc", "b"));
            }
        }
        """;

    public const string WinForms = """
        using System.Windows.Forms;

        public class DroidWinFormsScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                System.Windows.Forms.DialogResult answer = System.Windows.Forms.MessageBox.Show(
                    "Install?", "Prompt", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);
                bot.Log("answer=" + answer);
            }
        }
        """;

    // Touches the parts of the Skua API community scripts lean on: options, models, Ioc, the Option<T> type.
    public const string SkuaApi = """
        public class DroidSkuaApiScript
        {
            public List<IOption> Options = new() { new Option<bool>("x", "X", "desc", true) };

            public void ScriptMain(IScriptInterface bot)
            {
                var item = new InventoryItem();
                var quest = new Quest();
                var mon = new Monster();
                Func<IScriptInterface?> viaIoc = () => Ioc.Default.GetService<IScriptInterface>(); // compiled, not called
                bot.Log(item is not null && quest is not null && mon is not null ? "ok" : "bad");
            }
        }
        """;
}
