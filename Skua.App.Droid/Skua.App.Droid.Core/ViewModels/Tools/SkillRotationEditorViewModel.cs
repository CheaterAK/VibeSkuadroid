extern alias desktop;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Skills;
using Skua.Core.Models.Skills;
using System.Collections.ObjectModel;
using System.ComponentModel;
using AuraCheckViewModel = desktop::Skua.App.Avalonia.ViewModels.AdvancedSkills.AuraCheckViewModel;
using SkillItemViewModel = desktop::Skua.App.Avalonia.ViewModels.AdvancedSkills.SkillItemViewModel;
using SkillRulesViewModel = desktop::Skua.App.Avalonia.ViewModels.AdvancedSkills.SkillRulesViewModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One skill of the rotation being edited: the desktop's <c>SkillItemViewModel</c> (its text, its rules).</summary>
public sealed class SkillRow : ObservableObject
{
    internal SkillRow(SkillItemViewModel item, SkillRotationEditorViewModel owner)
    {
        Item = item;
        Owner = owner;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SkillItemViewModel.DisplayString))
                OnPropertyChanged(nameof(DisplayString));
        };
    }

    internal SkillItemViewModel Item { get; }

    public SkillRotationEditorViewModel Owner { get; }

    /// <summary>0 = auto attack, 1-4 = class skills, 5 = potion slot.</summary>
    public int Skill => Item.Skill;

    /// <summary>The desktop's description, e.g. "2 - [Health &gt; 70%] - [Skip if not available]".</summary>
    public string DisplayString => Item.DisplayString;

    /// <summary>Skua's form of this skill, e.g. "2 H&gt;70%S".</summary>
    public string Text => Item.Convert();
}

/// <summary>
/// Edits one rotation: class, mode, use mode, timeout, combo reset and the skill sequence with each skill's rules.
/// The phone form of the desktop's AdvancedSkillEditorViewModel: the skills are the desktop's own
/// <c>SkillItemViewModel</c>s (parsed and written back with its code), and the skill string is built the same way
/// (<c>string.Join(" | ", skill.Convert())</c>).
/// </summary>
public sealed partial class SkillRotationEditorViewModel : ObservableObject
{
    private readonly Func<SkillRotationEditorViewModel, Task> _save;

    /// <param name="original">The rotation being edited, or null for a new one.</param>
    /// <param name="className">Class name for a new rotation.</param>
    /// <param name="mode">Mode for a new rotation.</param>
    /// <param name="save">Saves the editor's rotation (sets <see cref="Error"/> on failure).</param>
    /// <exception cref="FormatException">A skill of <paramref name="original"/> cannot be read by the desktop's parser.</exception>
    public SkillRotationEditorViewModel(SkillSet? original, string? className, string? mode, Func<SkillRotationEditorViewModel, Task> save)
    {
        _save = save;
        Original = original;
        Skills.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Preview));
        if (original is null)
        {
            _className = className ?? string.Empty;
            _mode = mode ?? nameof(ClassUseMode.Base);
            _timeout = 250;
            return;
        }
        _className = original.ClassName;
        _mode = SkillSetStore.Modes.FirstOrDefault(m => string.Equals(m, original.Mode, StringComparison.OrdinalIgnoreCase)) ?? nameof(ClassUseMode.Base);
        _waitForCooldown = original.UseMode == SkillUseMode.WaitForCooldown;
        _timeout = original.Timeout;
        _resetComboOnTargetChange = original.ResetComboOnTargetChange;
        // As the desktop editor loads a rotation (AdvancedSkillEditorViewModel.EditSkill).
        foreach (string part in original.Skills.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string skill = part.Trim();
            if (skill.Length > 0)
                Add(Parse(skill));
        }
    }

    /// <summary>The rotation as it was opened (null: a new one).</summary>
    public SkillSet? Original { get; }

    public bool IsNew => Original is null;

    public string Title => IsNew ? "New rotation" : $"{Original!.ClassName} ({Original.Mode})";

    public IReadOnlyList<string> Modes => SkillSetStore.Modes;

    [ObservableProperty]
    private string _className;

    [ObservableProperty]
    private string _mode;

    /// <summary>"Wait for cooldown" (else "Use if available").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseIfAvailable))]
    private bool _waitForCooldown;

    public bool UseIfAvailable
    {
        get => !WaitForCooldown;
        set => WaitForCooldown = !value;
    }

    /// <summary>Skill timeout (wait for cooldown), ms.</summary>
    [ObservableProperty]
    private decimal? _timeout;

    [ObservableProperty]
    private bool _resetComboOnTargetChange;

    public ObservableCollection<SkillRow> Skills { get; } = new();

    /// <summary>The rules page of one skill while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesOpen), nameof(IsFormVisible))]
    private SkillRulesEditorViewModel? _rulesEditor;

    public bool IsRulesOpen => RulesEditor is not null;

    public bool IsFormVisible => RulesEditor is null;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    /// <summary>The skill string that will be saved (Skua's form).</summary>
    public string Preview => SkillString();

    /// <summary>As the desktop editor's SaveSkills builds it.</summary>
    public string SkillString() => string.Join(" | ", Skills.Select(s => s.Item.Convert()));

    /// <summary>The rotation to save.</summary>
    public SkillSet ToSkillSet() => new(
        ClassName.Trim(),
        Mode,
        SkillString(),
        WaitForCooldown ? SkillUseMode.WaitForCooldown : SkillUseMode.UseIfAvailable,
        (int)Math.Clamp(Timeout ?? 0, 0, int.MaxValue),
        ResetComboOnTargetChange);

    /// <summary>Adds skill 0 (auto attack) .. 5 (potion slot) at the end.</summary>
    [RelayCommand]
    private void AddSkill(string? skill)
    {
        if (int.TryParse(skill, out int index) && index is >= 0 and <= 5)
            Add(new SkillItemViewModel(index, new SkillRulesViewModel()));
    }

    [RelayCommand]
    private void MoveUp(SkillRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(SkillRow? row) => Move(row, 1);

    [RelayCommand]
    private void Remove(SkillRow? row)
    {
        if (row is not null)
            Skills.Remove(row);
    }

    [RelayCommand]
    private void ClearSkills() => Skills.Clear();

    /// <summary>Opens the rules of one skill (edited on a copy, as the desktop's rule dialog does).</summary>
    [RelayCommand]
    private void EditRules(SkillRow? row)
    {
        if (row is not null)
            RulesEditor = new SkillRulesEditorViewModel(row, () => RulesEditor = null);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => _save(this);

    private bool CanSave() => !IsSaving;

    /// <summary>Back inside the editor: closes the rules page; false when the form is shown.</summary>
    public bool GoBack()
    {
        if (RulesEditor is null)
            return false;
        RulesEditor = null;
        return true;
    }

    /// <summary>
    /// Moves a skill's trailing skip flag ("S") in front of its rules. The desktop parser
    /// (<c>SkillItemViewModel(string)</c>) reads a word after an aura's stack count as the aura's target, so for
    /// <c>1 A&lt;"Blood Boil" 0S</c> or <c>2 A&lt;"x" 1 TARGETS</c> it drops the skip flag (the desktop editor loses it
    /// on save). It reads "S" anywhere else, and Convert() writes it at the end again, so nothing else changes.
    /// </summary>
    /// <summary>
    /// The desktop's <c>SkillItemViewModel(string)</c>, with two of its parser's slips corrected so that opening and
    /// saving a rotation changes nothing (checked over every rotation of the community file): the skip flag after an
    /// aura (<see cref="ForDesktopParser"/>), and multi-aura checks after a "&gt;" one, which it reads as "&gt;" too
    /// even when written "&lt;" (their comparison is taken from Skua's own parser).
    /// </summary>
    internal static SkillItemViewModel Parse(string skill)
    {
        SkillItemViewModel item = new(ForDesktopParser(skill));
        List<Skua.Core.Models.Skills.SkillRuleJson> multi = (Skua.Core.Skills.AdvancedSkillsParser.ParseSkillString(skill).FirstOrDefault()?.Rules ?? [])
            .Where(r => r.Type == "MultiAura").ToList();
        if (multi.Count == item.UseRules.MultiAuraChecks.Count)
        {
            for (int i = 0; i < multi.Count; i++)
                item.UseRules.MultiAuraChecks[i].IsGreater = multi[i].Comparison == "greater";
        }
        return item;
    }

    internal static string ForDesktopParser(string skill)
    {
        if (skill.Length < 2 || skill[^1] != 'S')
            return skill;
        string rules = skill[1..^1].Trim();
        return rules.Length == 0 ? $"{skill[0]} S" : $"{skill[0]} S {rules}";
    }

    private void Add(SkillItemViewModel item)
    {
        SkillRow row = new(item, this);
        row.PropertyChanged += OnSkillChanged;
        Skills.Add(row);
    }

    private void Move(SkillRow? row, int direction)
    {
        if (row is null)
            return;
        int index = Skills.IndexOf(row);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= Skills.Count)
            return;
        Skills.Move(index, target);
    }

    private void OnSkillChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Preview));
}

/// <summary>One aura of a multi-aura rule (the desktop's <c>AuraCheckViewModel</c>).</summary>
public sealed class AuraCheckRow : ObservableObject
{
    internal AuraCheckRow(AuraCheckViewModel check, SkillRulesEditorViewModel owner)
    {
        Check = check;
        Owner = owner;
    }

    internal AuraCheckViewModel Check { get; }

    public SkillRulesEditorViewModel Owner { get; }

    public string AuraName
    {
        get => Check.AuraName;
        set
        {
            Check.AuraName = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>Stack count (the desktop ignores negative values).</summary>
    public float StackCount
    {
        get => Check.StackCount;
        set
        {
            Check.StackCount = value;
            OnPropertyChanged();
        }
    }

    public bool IsGreater
    {
        get => Check.IsGreater;
        set
        {
            Check.IsGreater = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0 = on you, 1 = on the target.</summary>
    public int TargetIndex
    {
        get => Check.AuraTargetIndex;
        set
        {
            Check.AuraTargetIndex = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// The rules of one skill: the desktop's <c>SkillRulesViewModel</c> on a copy, applied to the skill on Done
/// (<c>SkillItemViewModel.UseRules = copy</c>, as the desktop's rule dialog does).
/// </summary>
public sealed partial class SkillRulesEditorViewModel : ObservableObject
{
    private readonly SkillRow _row;
    private readonly Action _close;
    private readonly SkillRulesViewModel _rules;
    private bool _resetting;

    public SkillRulesEditorViewModel(SkillRow row, Action close)
    {
        _row = row;
        _close = close;
        _rules = new SkillRulesViewModel(row.Item.UseRules);
        foreach (AuraCheckViewModel check in _rules.MultiAuraChecks)
            AuraChecks.Add(new AuraCheckRow(check, this));
        _rules.PropertyChanged += OnRulesChanged;
    }

    public string Title => $"Skill {_row.Skill}: when to use it";

    /// <summary>The desktop's "Enable Rule": rules are only written when it is on (setting a condition turns it on).</summary>
    public bool UseRule
    {
        get => _rules.UseRuleBool;
        set => _rules.UseRuleBool = value;
    }

    /// <summary>Skip the skill when it is not available (rule suffix "S").</summary>
    public bool Skip
    {
        get => _rules.SkipUseBool;
        set => _rules.SkipUseBool = value;
    }

    /// <summary>Wait before use, ms ("WW").</summary>
    public int Wait
    {
        get => _rules.WaitUseValue;
        set => _rules.WaitUseValue = Math.Max(0, value);
    }

    public int Health
    {
        get => _rules.HealthUseValue;
        set => _rules.HealthUseValue = value;
    }

    /// <summary>1 = above, 0 = below.</summary>
    public int HealthComparison
    {
        get => _rules.HealthGreaterThanBool ? 1 : 0;
        set => _rules.HealthGreaterThanBool = value == 1;
    }

    /// <summary>0 = percent, 1 = points.</summary>
    public int HealthUnit
    {
        get => _rules.HealthIsPercentage ? 0 : 1;
        set => _rules.HealthIsPercentage = value == 0;
    }

    public int Mana
    {
        get => _rules.ManaUseValue;
        set => _rules.ManaUseValue = value;
    }

    public int ManaComparison
    {
        get => _rules.ManaGreaterThanBool ? 1 : 0;
        set => _rules.ManaGreaterThanBool = value == 1;
    }

    public int ManaUnit
    {
        get => _rules.ManaIsPercentage ? 0 : 1;
        set => _rules.ManaIsPercentage = value == 0;
    }

    public int PartyHealth
    {
        get => _rules.PartyMemberHealthUseValue;
        set => _rules.PartyMemberHealthUseValue = value;
    }

    public int PartyHealthComparison
    {
        get => _rules.PartyMemberHealthGreaterThanBool ? 1 : 0;
        set => _rules.PartyMemberHealthGreaterThanBool = value == 1;
    }

    public int PartyHealthUnit
    {
        get => _rules.PartyMemberHealthIsPercentage ? 0 : 1;
        set => _rules.PartyMemberHealthIsPercentage = value == 0;
    }

    /// <summary>Several auras (else one).</summary>
    public bool MultiAura
    {
        get => _rules.MultiAuraBool;
        set => _rules.MultiAuraBool = value;
    }

    /// <summary>0 = all of them (AND), 1 = any of them (OR).</summary>
    public int MultiAuraOperator
    {
        get => _rules.MultiAuraOperatorIndex;
        set => _rules.MultiAuraOperatorIndex = value;
    }

    public string AuraName
    {
        get => _rules.AuraName;
        set => _rules.AuraName = value ?? string.Empty;
    }

    public float AuraStacks
    {
        get => _rules.AuraUseValue;
        set => _rules.AuraUseValue = value;
    }

    /// <summary>1 = more than, 0 = fewer than.</summary>
    public int AuraComparison
    {
        get => _rules.AuraGreaterThanBool ? 1 : 0;
        set => _rules.AuraGreaterThanBool = value == 1;
    }

    /// <summary>0 = on you, 1 = on the target.</summary>
    public int AuraTarget
    {
        get => _rules.AuraTargetIndex;
        set => _rules.AuraTargetIndex = value;
    }

    public ObservableCollection<AuraCheckRow> AuraChecks { get; } = new();

    /// <summary>Applies the rules to the skill and closes.</summary>
    [RelayCommand]
    private void Done()
    {
        _row.Item.UseRules = _rules;
        _close();
    }

    [RelayCommand]
    private void Cancel() => _close();

    [RelayCommand]
    private void AddAuraCheck()
    {
        AuraCheckViewModel check = new();
        _rules.MultiAuraChecks.Add(check);
        AuraChecks.Add(new AuraCheckRow(check, this));
        MultiAura = true;
        EnableRules();
    }

    [RelayCommand]
    private void RemoveAuraCheck(AuraCheckRow? row)
    {
        if (row is null)
            return;
        AuraChecks.Remove(row);
        _rules.RemoveAuraCheckCommand.Execute(row.Check);
    }

    [RelayCommand]
    private void Clear()
    {
        _resetting = true;
        try
        {
            _rules.ResetUseRulesCommand.Execute(null);
            AuraChecks.Clear();
        }
        finally
        {
            _resetting = false;
        }
    }

    private static readonly Dictionary<string, string> s_names = new()
    {
        [nameof(SkillRulesViewModel.UseRuleBool)] = nameof(UseRule),
        [nameof(SkillRulesViewModel.SkipUseBool)] = nameof(Skip),
        [nameof(SkillRulesViewModel.WaitUseValue)] = nameof(Wait),
        [nameof(SkillRulesViewModel.HealthUseValue)] = nameof(Health),
        [nameof(SkillRulesViewModel.HealthGreaterThanBool)] = nameof(HealthComparison),
        [nameof(SkillRulesViewModel.HealthIsPercentage)] = nameof(HealthUnit),
        [nameof(SkillRulesViewModel.ManaUseValue)] = nameof(Mana),
        [nameof(SkillRulesViewModel.ManaGreaterThanBool)] = nameof(ManaComparison),
        [nameof(SkillRulesViewModel.ManaIsPercentage)] = nameof(ManaUnit),
        [nameof(SkillRulesViewModel.PartyMemberHealthUseValue)] = nameof(PartyHealth),
        [nameof(SkillRulesViewModel.PartyMemberHealthGreaterThanBool)] = nameof(PartyHealthComparison),
        [nameof(SkillRulesViewModel.PartyMemberHealthIsPercentage)] = nameof(PartyHealthUnit),
        [nameof(SkillRulesViewModel.MultiAuraBool)] = nameof(MultiAura),
        [nameof(SkillRulesViewModel.MultiAuraOperatorIndex)] = nameof(MultiAuraOperator),
        [nameof(SkillRulesViewModel.AuraName)] = nameof(AuraName),
        [nameof(SkillRulesViewModel.AuraUseValue)] = nameof(AuraStacks),
        [nameof(SkillRulesViewModel.AuraGreaterThanBool)] = nameof(AuraComparison),
        [nameof(SkillRulesViewModel.AuraTargetIndex)] = nameof(AuraTarget),
    };

    // The desktop only writes rules with "Enable Rule" ticked; here setting a condition ticks it.
    private static readonly HashSet<string> s_conditions =
    [
        nameof(SkillRulesViewModel.WaitUseValue), nameof(SkillRulesViewModel.HealthUseValue), nameof(SkillRulesViewModel.ManaUseValue),
        nameof(SkillRulesViewModel.PartyMemberHealthUseValue), nameof(SkillRulesViewModel.AuraName), nameof(SkillRulesViewModel.AuraUseValue),
        nameof(SkillRulesViewModel.SkipUseBool), nameof(SkillRulesViewModel.MultiAuraBool),
    ];

    private void OnRulesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is string name)
        {
            if (s_names.TryGetValue(name, out string? mine))
                OnPropertyChanged(mine);
            if (s_conditions.Contains(name))
                EnableRules();
        }
    }

    private void EnableRules()
    {
        if (!_resetting && !_rules.UseRuleBool && HasCondition())
            _rules.UseRuleBool = true;
    }

    private bool HasCondition() =>
        _rules.WaitUseValue != 0 || _rules.HealthUseValue != 0 || _rules.ManaUseValue != 0 || _rules.PartyMemberHealthUseValue != 0
        || !string.IsNullOrEmpty(_rules.AuraName) || _rules.AuraUseValue != 0 || _rules.SkipUseBool
        || (_rules.MultiAuraBool && _rules.MultiAuraChecks.Count > 0);
}
