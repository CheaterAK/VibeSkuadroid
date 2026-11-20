using CommunityToolkit.Mvvm.ComponentModel;
using Skua.App.Droid.Core.Engine;
using System.Collections.ObjectModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The phone's version of Skua's script options window: one editor per option the script declares (its Options
/// list and option groups), saved where Skua reads them (the script's options file).
/// </summary>
public sealed partial class ScriptOptionsViewModel : ObservableObject
{
    private readonly ScriptOptionSet _set;

    /// <param name="set">The options and their values.</param>
    /// <param name="scriptName">Shown in the header.</param>
    /// <param name="isCoreBots">CoreBots' own settings rather than one script's options.</param>
    /// <param name="profileOf">The queue entry whose profile this is (its own option storage), or null.</param>
    /// <param name="queueEntry">For a CoreBots profile: the queue entry it is saved to (instead of the CBO file).</param>
    public ScriptOptionsViewModel(ScriptOptionSet set, string scriptName, bool isCoreBots = false, string? profileOf = null, Guid? queueEntry = null)
    {
        _set = set;
        ScriptName = scriptName;
        IsCoreBots = isCoreBots;
        ProfileOf = profileOf;
        QueueEntryId = queueEntry;
        foreach (ScriptOptionItem item in set.Items)
            Items.Add(new ScriptOptionEditor(item, set.Items.Any(i => i.Category != "Options")));
    }

    public string ScriptName { get; }

    /// <summary>CoreBots' own settings (CBO file) rather than one script's options.</summary>
    public bool IsCoreBots { get; }

    /// <summary>The queue entry this profile belongs to, or null for the script's (or account's) own options.</summary>
    public string? ProfileOf { get; }

    /// <summary>A queue entry's CoreBots profile: saved in the queue, written to the CBO file before each run of the entry.</summary>
    public Guid? QueueEntryId { get; }

    public string Storage => _set.Storage;

    public string Header => ProfileOf is string entry ? $"{ScriptName} options for queue entry \"{entry}\"" : $"{ScriptName} options";

    public string StorageText => (IsCoreBots, QueueEntryId) switch
    {
        (true, not null) => $"Saved with the queue entry; written to {_set.Storage}.txt right before each of its runs. Empty means CoreBots' default.",
        (true, null) => $"Saved as {_set.Storage}.txt in Skua's options folder; every CoreBots script reads it at start. Empty means CoreBots' default.",
        _ when ProfileOf is not null => $"This entry's own options, saved as {_set.Storage}.cfg in Skua's options folder (the script's own options are not changed).",
        _ => $"Saved as {_set.Storage}.cfg in Skua's options folder; read at every start.",
    };

    public ObservableCollection<ScriptOptionEditor> Items { get; } = new();

    [ObservableProperty]
    private string? _error;

    /// <summary>The edited values.</summary>
    public ScriptOptionSet ToSet() => _set with { Items = Items.Select(e => e.Item with { Value = e.Value }).ToList() };
}

/// <summary>One option in the editor.</summary>
public sealed partial class ScriptOptionEditor : ObservableObject
{
    public ScriptOptionEditor(ScriptOptionItem item, bool showCategory)
    {
        Item = item;
        _value = item.Value;
        ShowCategory = showCategory && item.Category != "Options";
    }

    public ScriptOptionItem Item { get; }

    public string Title => Item.Title;

    public string Description => Item.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description) && Item.Description != "null";

    public string Category => Item.Category.Replace('_', ' ');

    public bool ShowCategory { get; }

    public bool IsToggle => Item.Kind == ScriptOptionKind.Toggle;

    public bool IsChoice => Item.Kind == ScriptOptionKind.Choice;

    public bool IsNumber => Item.Kind == ScriptOptionKind.Number;

    public bool IsText => Item.Kind is ScriptOptionKind.Text or ScriptOptionKind.Number;

    public bool IsTransient => Item.Transient;

    public bool IsEditable => !Item.Transient;

    public IReadOnlyList<string> Choices => Item.Choices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoolValue), nameof(SelectedChoice))]
    private string _value;

    public bool BoolValue
    {
        get => string.Equals(Value, "True", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "True" : "False";
    }

    public string? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => string.Equals(c, Value, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                Value = value;
        }
    }
}
