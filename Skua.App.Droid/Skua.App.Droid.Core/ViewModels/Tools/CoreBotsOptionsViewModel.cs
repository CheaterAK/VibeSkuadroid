extern alias desktop;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Scripts;
using Skua.Core.Interfaces;
using Skua.Core.Models.Skills;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using CBOClassEquipmentViewModel = desktop::Skua.App.Avalonia.ViewModels.CoreBotsOptions.CBOClassEquipmentViewModel;
using CBOClassSelectViewModel = desktop::Skua.App.Avalonia.ViewModels.CoreBotsOptions.CBOClassSelectViewModel;
using CBOLoadoutViewModel = desktop::Skua.App.Avalonia.ViewModels.CoreBotsOptions.CBOLoadoutViewModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One CoreBots setting on the Options or Other tab (the desktop's option items).</summary>
public sealed partial class CoreBotsOption : ObservableObject
{
    public CoreBotsOption(string group, string key, string title, string? description, ScriptOptionKind kind, string defaultValue)
    {
        Group = group;
        Key = key;
        Title = title;
        Description = description;
        Kind = kind;
        Default = defaultValue;
        Reset();
    }

    public string Group { get; }

    /// <summary>The key in the settings file.</summary>
    public string Key { get; }

    public string Title { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public ScriptOptionKind Kind { get; }

    public string Default { get; }

    public bool IsToggle => Kind == ScriptOptionKind.Toggle;

    public bool IsNumber => Kind == ScriptOptionKind.Number;

    public bool IsText => Kind == ScriptOptionKind.Text;

    public bool IsTextual => !IsToggle;

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private string _textValue = string.Empty;

    /// <summary>The value as the desktop writes it ("True"/"False", the number, the text).</summary>
    public string Value => IsToggle ? (BoolValue ? "True" : "False") : TextValue.Trim();

    public void Reset() => Set(Default);

    /// <summary>Sets the value read from the file; one that does not convert falls back to the default (as the desktop's SetValues).</summary>
    public void Set(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            value = Default;
        switch (Kind)
        {
            case ScriptOptionKind.Toggle:
                BoolValue = bool.TryParse(value.Trim(), out bool b) ? b : bool.Parse(Default);
                break;
            case ScriptOptionKind.Number:
                // CBOptionsViewModel.SetValues: a number outside int becomes 100000 (the room number's default).
                TextValue = long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                    ? (l is > int.MaxValue or < int.MinValue ? 100000 : l).ToString(CultureInfo.InvariantCulture)
                    : Default;
                break;
            default:
                TextValue = value;
                break;
        }
    }
}

/// <summary>A heading of the Other tab with its settings.</summary>
public sealed record CoreBotsOptionGroup(string Title, IReadOnlyList<CoreBotsOption> Options);

/// <summary>
/// One loadout slot (Solo, Farm, Dodge, Boss): class, its rotation (mode), gear switching and the gear. A view of the
/// desktop's <c>CBOClassSelectViewModel</c> and <c>CBOClassEquipmentViewModel</c> (their Selected{Slot}... properties).
/// </summary>
public sealed class CoreBotsSlot : ObservableObject
{
    private readonly CBOClassSelectViewModel _classes;
    private readonly CBOClassEquipmentViewModel _equipment;

    internal CoreBotsSlot(string name, string description, CBOClassSelectViewModel classes, CBOClassEquipmentViewModel equipment)
    {
        Name = name;
        Description = description;
        _classes = classes;
        _equipment = equipment;
    }

    /// <summary>Solo, Farm, Dodge or Boss.</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>The character's classes (enhanced ones, as the desktop lists them) and "[Current]".</summary>
    public IReadOnlyList<string> ClassChoices => _classes.PlayerClasses.ToList();

    public string? ClassName
    {
        get => Get<string?>(_classes, $"Selected{Name}Class");
        set
        {
            if (value == ClassName)
                return;
            Set(_classes, $"Selected{Name}Class", value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeChoices));
            OnPropertyChanged(nameof(Mode));
        }
    }

    /// <summary>The modes the class has rotations for (Advanced Skills), else Skua's modes.</summary>
    public IReadOnlyList<string> ModeChoices
    {
        get
        {
            List<string> modes = Get<List<string>>(_classes, $"{Name}ModeStrings") ?? [];
            return modes.Count > 0 ? modes.ToList() : Enum.GetNames<ClassUseMode>();
        }
    }

    /// <summary>The rotation CoreBots uses for this slot ({Slot}ModeSelect).</summary>
    public string? Mode
    {
        get => Get<ClassUseMode?>(_classes, $"Selected{Name}UseMode")?.ToString();
        set
        {
            if (value is null || value == Mode)
                return;
            if (Enum.TryParse(value, true, out ClassUseMode mode))
                Set<ClassUseMode?>(_classes, $"Selected{Name}UseMode", mode);
            OnPropertyChanged();
        }
    }

    /// <summary>Equip this slot's gear when switching to it ({Slot}EquipCheck).</summary>
    public bool UseEquipment
    {
        get => Get<bool>(_classes, $"Use{Name}Equipment");
        set
        {
            Set(_classes, $"Use{Name}Equipment", value);
            OnPropertyChanged();
        }
    }

    public string? Helm { get => Gear("Helm"); set => SetGear("Helm", value); }

    public string? Armor { get => Gear("Armor"); set => SetGear("Armor", value); }

    public string? Cape { get => Gear("Cape"); set => SetGear("Cape", value); }

    public string? Weapon { get => Gear("Weapon"); set => SetGear("Weapon", value); }

    public string? Pet { get => Gear("Pet"); set => SetGear("Pet", value); }

    public string? GroundItem { get => Gear("GroundItem"); set => SetGear("GroundItem", value); }

    /// <summary>Owned items per kind (the desktop's lists), with "" (none) first.</summary>
    public IReadOnlyList<string> Helms => Choices(_equipment.Helms);

    public IReadOnlyList<string> Armors => Choices(_equipment.Armors);

    public IReadOnlyList<string> Capes => Choices(_equipment.Capes);

    public IReadOnlyList<string> Weapons => Choices(_equipment.Weapons);

    public IReadOnlyList<string> Pets => Choices(_equipment.Pets);

    public IReadOnlyList<string> GroundItems => Choices(_equipment.GroundItems);

    /// <summary>After the desktop ViewModels were reloaded.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private static List<string> Choices(List<string> items) => [string.Empty, .. items.Distinct(StringComparer.Ordinal)];

    // Selected{Slot}{Kind}: the slot's gear in the desktop's equipment ViewModel ("" for none).
    private string? Gear(string kind) => Get<string?>(_equipment, $"Selected{Name}{kind}") ?? string.Empty;

    private void SetGear(string kind, string? value)
    {
        Set(_equipment, $"Selected{Name}{kind}", string.IsNullOrEmpty(value) ? null : value);
        OnPropertyChanged(kind);
    }

    private static T? Get<T>(object target, string property) => (T?)Property(target, property).GetValue(target);

    private static void Set<T>(object target, string property, T value) => Property(target, property).SetValue(target, value);

    private static PropertyInfo Property(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException(target.GetType().Name, name);
}

/// <summary>
/// CoreBots options (the desktop's CoreBots window: Loadout, Options, Other) for the logged-in account, written to
/// <c>options/CBO_Storage(&lt;account&gt;).txt</c> where CoreBots reads them. The loadout is the desktop's own
/// <c>CBOLoadoutViewModel</c> (class selection from the character's classes, modes from the skill sets, gear from the
/// inventory) on Skua's services of the engine's session; Options and Other are the desktop's option lists.
/// </summary>
/// <remarks>
/// The file is written as the desktop writes it (loadout, options, other, one <c>Key: Value</c> line each, in the
/// desktop's order). Unlike the desktop, lines it does not know are kept (settings of other Core files, or set in the
/// script options editor), after the known ones, unless CoreBots would read a known line for them anyway (it matches
/// keys by prefix: "ActionDelay" reads "ActionDelayNr").
/// </remarks>
public sealed partial class CoreBotsOptionsViewModel : ObservableObject, IToolPage
{
    // CoreBots reads the desktop's ActionDelayNr line under the key ActionDelay (prefix match); the old editor wrote that key.
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal) { ["ActionDelayNr"] = "ActionDelay" };

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private CBOClassSelectViewModel? _classes;
    private CBOClassEquipmentViewModel? _equipment;
    private CBOLoadoutViewModel? _loadout;
    private bool _publicRoomsConfirmed;

    public CoreBotsOptionsViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        General = GeneralOptions();
        Other = OtherOptions();
    }

    public string Title => "CoreBots options";

    /// <summary>The desktop's Options tab (CoreBots.CreateOptions), in its order.</summary>
    public IReadOnlyList<CoreBotsOption> General { get; }

    /// <summary>The desktop's Other tab (CoreBots.CreateOtherOptions).</summary>
    public IReadOnlyList<CoreBotsOptionGroup> Other { get; }

    /// <summary>Settings CoreBots reads (from the synced Core*.cs) that no tab covers.</summary>
    public ObservableCollection<CoreBotsOption> More { get; } = new();

    public bool HasMore => More.Count > 0;

    public ObservableCollection<CoreBotsSlot> Slots { get; } = new();

    /// <summary>Classes, Equipment, Options or Other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClassesTab), nameof(IsEquipmentTab), nameof(IsOptionsTab), nameof(IsOtherTab))]
    private string _tab = "Classes";

    public bool IsClassesTab => Tab == "Classes";

    public bool IsEquipmentTab => Tab == "Equipment";

    public bool IsOptionsTab => Tab == "Options";

    public bool IsOtherTab => Tab == "Other";

    /// <summary>The account whose file is edited, or null (not logged in).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountText), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(LoadCharacterCommand), nameof(ReloadCommand))]
    private string? _account;

    /// <summary>Skua's services exist (the loadout needs the character's inventory).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(LoadCharacterCommand), nameof(ReloadCommand))]
    private bool _hasSession;

    public bool IsReady => Account is not null && HasSession;

    public string AccountText => Account is null ? "Log in first: the options are saved per account." : $"Account: {Account}";

    [ObservableProperty]
    private string? _message;

    /// <summary>Private rooms were switched off: the desktop's warning, answered before saving.</summary>
    [ObservableProperty]
    private bool _confirmPublicRooms;

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (tab is "Classes" or "Equipment" or "Options" or "Other")
            Tab = tab;
    }

    public void Activate()
    {
        Message = null;
        if (IsReady)
            Load(refreshCharacter: true);
        else if (Account is not null && !HasSession)
            Message = "CoreBots options need the Skua script host.";
    }

    public bool GoBack()
    {
        if (ConfirmPublicRooms)
        {
            ConfirmPublicRooms = false;
            return true;
        }
        return false;
    }

    internal void Apply(EngineSnapshot s)
    {
        string? account = s.Character?.Name ?? s.AccountName;
        if (account is not null && !CoreBotsOptions.IsSafeAccountName(account))
            account = null;
        HasSession = _host.Engine.SkuaServices is not null;
        // The typed account name first, then the character's name as the game spells it: the same file.
        if (string.Equals(account, Account, StringComparison.OrdinalIgnoreCase))
        {
            Account = account;
        }
        else
        {
            Account = account;
            _loadout = null;
            Slots.Clear();
        }
    }

    private string OptionsDir => Path.Combine(_services.Paths.SkuaDir, "options");

    /// <summary>Reads the file again (selections not saved are dropped).</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void Reload() => Load(refreshCharacter: false);

    /// <summary>The desktop's Load Character: classes and gear from the inventory, keeping the selections.</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void LoadCharacter()
    {
        if (!EnsureLoadout())
            return;
        Dictionary<string, string> selections = DesktopValues(LoadoutLines());
        _classes!.ReloadClassesCommand.Execute(null);
        _equipment!.RefreshInventoryCommand.Execute(null);
        _loadout!.SetValues(selections);
        RefreshSlots();
        Message = $"{_classes.PlayerClasses.Count - 1} classes and the gear of {Account} loaded.";
    }

    private void Load(bool refreshCharacter)
    {
        try
        {
            if (!EnsureLoadout())
                return;
            Dictionary<string, string> values = CoreBotsOptions.Values(CoreBotsOptions.ReadLines(OptionsDir, Account!));
            if (refreshCharacter)
                _equipment!.RefreshInventoryCommand.Execute(null);
            _loadout!.SetValues(DesktopValues(values.Select(v => $"{v.Key}: {v.Value}"))); // reloads the classes from the inventory (desktop)
            foreach (CoreBotsOption option in General.Concat(Other.SelectMany(g => g.Options)))
                option.Set(ValueFor(values, option.Key));
            LoadMore(values);
            RefreshSlots();
            _publicRoomsConfirmed = false;
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
    }

    // As the desktop window reads the file (CoreBotsViewModel.ReadValues): a line with an empty value is no value.
    private static Dictionary<string, string> DesktopValues(IEnumerable<string> lines) =>
        CoreBotsOptions.Values(lines).Where(v => v.Value.Length > 0).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static string? ValueFor(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? v) ? v
        : s_aliases.TryGetValue(key, out string? alias) && values.TryGetValue(alias, out string? a) ? a
        : null;

    /// <summary>Writes the file; asks first when private rooms are off (the desktop's warning).</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void Save()
    {
        if (!EnsureLoadout())
            return;
        CoreBotsOption privateRooms = General.Single(o => o.Key == "PrivateRooms");
        if (!privateRooms.BoolValue && !_publicRoomsConfirmed)
        {
            ConfirmPublicRooms = true;
            return;
        }
        List<string> problems = General.Concat(More)
            .Where(o => o.IsNumber && o.TextValue.Trim().Length > 0 && !int.TryParse(o.TextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(o => $"{o.Title}: \"{o.TextValue}\" is not a whole number")
            .ToList();
        if (problems.Count > 0)
        {
            Message = "Not saved. " + string.Join("; ", problems);
            return;
        }
        try
        {
            List<string> lines = BuildLines(CoreBotsOptions.ReadLines(OptionsDir, Account!));
            CoreBotsOptions.WriteLines(OptionsDir, Account!, lines);
            Message = $"Saved to options/CBO_Storage({Account}).txt. Scripts read it when they start.";
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Not saved: {e.Message}";
        }
    }

    [RelayCommand]
    private void UsePublicRooms()
    {
        _publicRoomsConfirmed = true;
        ConfirmPublicRooms = false;
        Save();
    }

    /// <summary>The desktop's answer "No" to its warning: saved with private rooms.</summary>
    [RelayCommand]
    private void KeepPrivateRooms()
    {
        General.Single(o => o.Key == "PrivateRooms").BoolValue = true;
        ConfirmPublicRooms = false;
        Save();
    }

    /// <summary>
    /// The file's lines: the desktop's (loadout, options, other, in its order), then the settings of
    /// <see cref="More"/> that are set, then lines of <paramref name="existing"/> no tab wrote.
    /// </summary>
    internal List<string> BuildLines(IReadOnlyList<string> existing)
    {
        StringBuilder builder = new();
        _loadout!.Save(builder);
        foreach (CoreBotsOption option in General.Concat(Other.SelectMany(g => g.Options)))
            builder.AppendLine($"{option.Key}: {option.Value}");
        List<string> lines = builder.ToString().Split(Environment.NewLine).Where(l => l.Length > 0).ToList();

        HashSet<string> written = lines.Select(CoreBotsOptions.KeyOfLine).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> previous = CoreBotsOptions.Values(existing);
        foreach (CoreBotsOption option in More)
        {
            bool set = option.IsToggle ? option.BoolValue || previous.ContainsKey(option.Key) : option.Value.Length > 0;
            if (set && written.Add(option.Key))
                lines.Add($"{option.Key}: {option.Value}");
        }
        foreach (string line in existing)
        {
            string key = CoreBotsOptions.KeyOfLine(line);
            if (key.Length == 0 || Shadowed(key, written))
                continue;
            written.Add(key);
            lines.Add(line);
        }
        return lines;
    }

    // CoreBots takes the first line that starts with the key it looks for.
    private static bool Shadowed(string key, HashSet<string> written) =>
        written.Any(w => w.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(w, StringComparison.Ordinal));

    private IEnumerable<string> LoadoutLines()
    {
        StringBuilder builder = new();
        _loadout!.Save(builder);
        return builder.ToString().Split(Environment.NewLine);
    }

    private bool EnsureLoadout()
    {
        if (_loadout is not null)
            return true;
        if (Account is null)
        {
            Message = "Log in first: the options are saved per account.";
            return false;
        }
        IServiceProvider? skua = _host.Engine.SkuaServices;
        if (skua?.GetService(typeof(IScriptInventory)) is not IScriptInventory inventory
            || skua.GetService(typeof(IAdvancedSkillContainer)) is not IAdvancedSkillContainer skills
            || skua.GetService(typeof(IScriptPlayer)) is not IScriptPlayer player)
        {
            Message = "CoreBots options need the Skua script host.";
            return false;
        }
        _classes = new CBOClassSelectViewModel(inventory, skills, player);
        _equipment = new CBOClassEquipmentViewModel(inventory);
        _loadout = new CBOLoadoutViewModel(_classes, _equipment);
        Slots.Clear();
        Slots.Add(new CoreBotsSlot("Solo", "Bosses and hard fights.", _classes, _equipment));
        Slots.Add(new CoreBotsSlot("Farm", "Farming many monsters.", _classes, _equipment));
        Slots.Add(new CoreBotsSlot("Dodge", "Fights where you must dodge.", _classes, _equipment));
        Slots.Add(new CoreBotsSlot("Boss", "Ultra bosses.", _classes, _equipment));
        return true;
    }

    private void RefreshSlots()
    {
        foreach (CoreBotsSlot slot in Slots)
            slot.Refresh();
    }

    // Keys CoreBots reads (from the synced Core*.cs) that no tab writes (CoreBots matches by prefix: a desktop key
    // that starts with the key covers it).
    private void LoadMore(Dictionary<string, string> values)
    {
        HashSet<string> known = General.Concat(Other.SelectMany(g => g.Options)).Select(o => o.Key)
            .Concat(LoadoutLines().Select(CoreBotsOptions.KeyOfLine)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        More.Clear();
        foreach ((string key, ScriptOptionKind kind) in CoreBotsOptions.ReadKeys(_services.Library.Root))
        {
            if (known.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                continue;
            CoreBotsOption option = new("More", key, System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " "), null, kind,
                kind == ScriptOptionKind.Toggle ? "False" : string.Empty);
            option.Set(values.TryGetValue(key, out string? v) ? v : null);
            More.Add(option);
        }
        OnPropertyChanged(nameof(HasMore));
    }

    private static CoreBotsOption Bool(string group, string title, string key, bool value = false, string? description = null) =>
        new(group, key, title, description, ScriptOptionKind.Toggle, value ? "True" : "False");

    private static CoreBotsOption Int(string title, string key, int value) =>
        new("Options", key, title, null, ScriptOptionKind.Number, value.ToString(CultureInfo.InvariantCulture));

    // Skua.App.Avalonia/AppStartup/CoreBots.cs, CreateOptions (order kept: it is the file's order).
    internal static IReadOnlyList<CoreBotsOption> GeneralOptions() =>
    [
        Bool("Options", "Private Rooms", "PrivateRooms", true),
        Bool("Options", "Public on Difficult Parts", "PublicDifficult"),
        Bool("Options", "Bank Misc. AC items on Start-Up", "BankMiscAC", true),
        Bool("Options", "Logger in Chat", "LoggerInChat", true),
        Bool("Options", "Force Off MessageBoxes", "MessageBoxCheck"),
        Bool("Options", "Should Rest", "RestCheck"),
        Bool("Options", "Disable AutoEnhance", "DisableAutoEnhance"),
        Bool("Options", "Disable BestGear", "DisableBestGear"),
        Bool("Options", "Anti Lag", "AntiLag", true),
        Bool("Options", "Incognito Mode", "IncognitoMode", true),
        Int("Room Number", "PrivateRoomNr", 100000),
        Int("Action Delay", "ActionDelayNr", 700),
        Int("Exit Combat Delay", "ExitCombatNr", 1600),
        Int("Hunt Delay", "HuntDelayNr", 100),
        Int("Accept and Complete Tries", "QuestTriesNr", 20),
        Int("Loaded Quests Max.", "QuestMaxNr", 150),
        new("Options", "StopLocationSelect", "Map after stopping the Bot", null, ScriptOptionKind.Text, "Home"),
    ];

    // Skua.App.Avalonia/AppStartup/CoreBots.cs, CreateOtherOptions.
    internal static IReadOnlyList<CoreBotsOptionGroup> OtherOptions() =>
    [
        new("Boosters",
        [
            Bool("Boosters", "Use Gold Boosts when farming gold", "doGoldBoost"),
            Bool("Boosters", "Use Class Boosts when ranking up a class", "doClassBoost"),
            Bool("Boosters", "Use Reputation Boosts when farming REP", "doRepBoost"),
            Bool("Boosters", "Use Experience Boosts when farming EXP", "doExpBoost"),
        ]),
        new("Nation Farms",
        [
            Bool("Nation Farms", "Sell Voucher of Nulgath if not needed", "Nation_SellMemVoucher", true),
            Bool("Nation Farms", "Do Swindles Return during Supplies", "Nation_ReturnPolicyDuringSupplies", true),
            Bool("Nation Farms", "Ultra Alteon for Supplies", "UltraAlteonForSupplies", false, "Use Ultra Alteon when farming Supplies instead of Escherion"),
        ]),
        new("Bludrut Brawl (PvP)",
        [
            Bool("Bludrut Brawl (PvP)", "Kill ads before boss", "PvP_SoloPvPBoss", false, "Whether to kill brawlers and restorers in the PvP before the boss."),
        ]),
        new("Bot Creators Only",
        [
            Bool("Bot Creators Only", "Story Bot Test Mode", "BCO_Story_TestBot", false, "Toggles innate isCompleteBefore checks. It remains on the for Complete Once Quests"),
        ]),
    ];
}
