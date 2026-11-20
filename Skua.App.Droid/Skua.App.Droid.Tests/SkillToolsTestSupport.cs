using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.Core.Headless;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Models.Skills;
using System.ComponentModel;
using System.Reflection;

namespace Skua.App.Droid.Tests;

/// <summary>A script host without scripts that hands the engine Skua services (fakes) for the Tools pages.</summary>
internal sealed class ToolsScriptHost : IScriptHost, ISkuaServicesSource
{
    private readonly UnavailableScriptHost _inner = new();

    public ToolsScriptHost(IServiceProvider services)
    {
        SkuaServices = services;
    }

    public IServiceProvider? SkuaServices { get; }

    public ScriptSupport Support => _inner.Support;

    public ScriptStatus Status => _inner.Status;

    public int LoadedAssemblyCount => 0;

    public event EventHandler<ScriptStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
    }

    public void Load(string scriptPath) => _inner.Load(scriptPath);

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A Skua interface whose properties answer from a table (everything else: default).</summary>
public class SkuaStub : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = new();

    public static T Create<T>(Dictionary<string, object?> values) where T : class
    {
        T proxy = Create<T, SkuaStub>();
        foreach ((string key, object? value) in values)
            ((SkuaStub)(object)proxy).Values[key] = value;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) && Values.TryGetValue(targetMethod.Name[4..], out object? value))
            return value;
        Type returnType = targetMethod.ReturnType;
        return returnType != typeof(void) && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>Skill sets in memory (what the CoreBots class page reads the modes from).</summary>
internal sealed class FakeSkillContainer : IAdvancedSkillContainer
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public List<AdvancedSkill> LoadedSkills { get; set; } = new();

    public List<AdvancedSkill> Added { get; } = new();

    public void Add(AdvancedSkill skill)
    {
        Added.Add(skill);
        LoadedSkills.Add(skill);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadedSkills)));
    }

    public void TryOverride(AdvancedSkill skill) => Add(skill);

    public void LoadSkills()
    {
    }

    public void ResetSkillsSets()
    {
    }

    public void SyncSkills()
    {
    }

    public void Remove(AdvancedSkill skill) => LoadedSkills.Remove(skill);

    public void Save()
    {
    }

    public Dictionary<string, List<string>> GetAvailableClassModes() => LoadedSkills
        .GroupBy(s => s.ClassName)
        .ToDictionary(g => g.Key, g => g.Select(s => s.ClassUseMode.ToString()).ToList());

    public AdvancedSkill? GetClassModeSkills(string className, string mode) =>
        LoadedSkills.FirstOrDefault(s => s.ClassName == className && s.ClassUseMode.ToString() == mode);
}

/// <summary>A character's inventory and skill sets for the CoreBots pages.</summary>
internal static class FakeCharacter
{
    public static InventoryItem Item(string name, string category, int enhancement = 1, string? group = null) =>
        new() { Name = name, CategoryString = category, EnhancementLevel = enhancement, ItemGroup = group ?? string.Empty };

    public static IServiceProvider Services(out FakeSkillContainer skills, string currentClass = "Void Highlord")
    {
        List<InventoryItem> items =
        [
            Item("Void Highlord", "Class"),
            Item("ArchPaladin", "Class"),
            Item("Unenhanced Class", "Class", enhancement: 0),
            Item("Sample Helm", "Helm"),
            Item("Sample Armor", "Armor", enhancement: 0),
            Item("Sample Cape", "Cape"),
            Item("Sample Sword", "Sword", group: "Weapon"),
            Item("Sample Pet", "Pet", enhancement: 0),
            Item("Sample Ground", "Misc", enhancement: 0),
        ];
        skills = new FakeSkillContainer();
        skills.LoadedSkills.Add(new AdvancedSkill("Void Highlord", "1 | 2 | 3 | 4", 100, ClassUseMode.Base, SkillUseMode.UseIfAvailable));
        skills.LoadedSkills.Add(new AdvancedSkill("Void Highlord", "4 | 3", 100, ClassUseMode.Solo, SkillUseMode.WaitForCooldown));
        skills.LoadedSkills.Add(new AdvancedSkill("ArchPaladin", "1 | 2", 100, ClassUseMode.Farm, SkillUseMode.UseIfAvailable));
        return new ServiceCollection()
            .AddSingleton(SkuaStub.Create<IScriptInventory>(new() { ["Items"] = items }))
            .AddSingleton(SkuaStub.Create<IScriptPlayer>(new() { ["CurrentClass"] = items.First(i => i.Name == currentClass) }))
            .AddSingleton<IAdvancedSkillContainer>(skills)
            .BuildServiceProvider();
    }
}
