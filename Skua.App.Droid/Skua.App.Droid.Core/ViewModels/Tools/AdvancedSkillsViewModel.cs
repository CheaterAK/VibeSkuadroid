using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Skills;
using Skua.Core.Interfaces;
using Skua.Core.Models.Skills;
using System.Collections.ObjectModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A class in the Advanced Skills list with its rotations.</summary>
public sealed class SkillClassItem
{
    public SkillClassItem(string className, IReadOnlyList<SkillSetRow> sets, AdvancedSkillsViewModel owner)
    {
        ClassName = className;
        Sets = sets;
        Owner = owner;
    }

    public AdvancedSkillsViewModel Owner { get; }

    public string ClassName { get; }

    public IReadOnlyList<SkillSetRow> Sets { get; }

    public string ModesText => string.Join(", ", Sets.Select(s => s.Set.Mode));

    /// <summary>"edited" / "yours" when any rotation differs from the community file.</summary>
    public string? Badge => Sets.Any(s => s.Set.Origin == SkillSetOrigin.Yours) ? "yours"
        : Sets.Any(s => s.Set.Origin == SkillSetOrigin.Edited) ? "edited"
        : null;

    public bool HasBadge => Badge is not null;
}

/// <summary>One rotation on a class's page, with its actions.</summary>
public sealed class SkillSetRow
{
    public SkillSetRow(SkillSet set, AdvancedSkillsViewModel owner)
    {
        Set = set;
        Owner = owner;
    }

    public SkillSet Set { get; }

    public AdvancedSkillsViewModel Owner { get; }

    public string Mode => Set.Mode;

    public string Skills => Set.Skills;

    public string UseModeText => Set.UseModeText + (Set.ResetComboOnTargetChange ? ", reset on target change" : string.Empty);

    public string OriginText => Set.Origin switch
    {
        SkillSetOrigin.Community => "community",
        SkillSetOrigin.Edited => "edited",
        _ => "yours",
    };

    /// <summary>The community file has another version of it.</summary>
    public bool CanRestore => Set.Origin == SkillSetOrigin.Edited;
}

/// <summary>
/// Advanced Skills (the desktop's AdvancedSkillsView): the classes and their rotations from Skua's skill sets
/// (<c>UserAdvancedSkills.json</c>, what scripts use), and an editor for one rotation. Saves go to that file
/// (<see cref="SkillSetStore"/>) and Skua's container is reloaded, so the next script that loads a class's skills
/// uses them (as on the desktop, a running script keeps the rotation it already loaded).
/// </summary>
public sealed partial class AdvancedSkillsViewModel : ObservableObject, IToolPage
{
    private readonly EngineHost _host;
    private readonly SkillSetStore _store;
    private List<SkillSet> _all = new();

    public AdvancedSkillsViewModel(AppServices services)
    {
        _host = services.Host;
        _store = new SkillSetStore(services.Paths.SkuaDir);
    }

    /// <summary>The classes matching <see cref="SearchText"/>.</summary>
    public ObservableCollection<SkillClassItem> Classes { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The class whose rotations are shown, or null for the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsListVisible), nameof(IsClassVisible))]
    private SkillClassItem? _selectedClass;

    /// <summary>The rotation editor while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsListVisible), nameof(IsClassVisible), nameof(IsEditorVisible))]
    private SkillRotationEditorViewModel? _editor;

    /// <summary>A rotation waiting for the user to confirm its deletion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletePrompt))]
    private SkillSetRow? _pendingDelete;

    [ObservableProperty]
    private bool _confirmReset;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string _summary = string.Empty;

    public string Title => Editor is not null ? Editor.Title : SelectedClass?.ClassName ?? "Advanced Skills";

    public bool IsListVisible => Editor is null && SelectedClass is null;

    public bool IsClassVisible => Editor is null && SelectedClass is not null;

    public bool IsEditorVisible => Editor is not null;

    public string? DeletePrompt => PendingDelete is null ? null : $"Delete {PendingDelete.Set.ClassName} ({PendingDelete.Mode})? Scripts then use another mode of the class, or generic skills.";

    /// <summary>The rotations as last read (for tests and the CoreBots class page).</summary>
    public IReadOnlyList<SkillSet> All => _all;

    public void Activate() => _ = RefreshAsync();

    public bool GoBack()
    {
        if (Editor is not null)
        {
            if (!Editor.GoBack())
                Editor = null;
            return true;
        }
        if (PendingDelete is not null || ConfirmReset)
        {
            PendingDelete = null;
            ConfirmReset = false;
            return true;
        }
        if (SelectedClass is not null)
        {
            SelectedClass = null;
            return true;
        }
        return false;
    }

    /// <summary>Brings the skill sets up to date with the community file (keeping edits) and reads them.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            List<SkillSet> sets = await Task.Run(() =>
            {
                if (_store.UpdateFromCommunity())
                    ReloadSkua();
                return _store.Load().ToList();
            });
            Show(sets);
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => Filter();

    [RelayCommand]
    private void OpenClass(SkillClassItem? item)
    {
        PendingDelete = null;
        SelectedClass = item;
    }

    /// <summary>A new rotation (for the open class: its first mode without one).</summary>
    [RelayCommand]
    private void New()
    {
        string? className = SelectedClass?.ClassName;
        string mode = className is null
            ? nameof(ClassUseMode.Base)
            : SkillSetStore.Modes.FirstOrDefault(m => SelectedClass!.Sets.All(s => !string.Equals(s.Mode, m, StringComparison.OrdinalIgnoreCase))) ?? nameof(ClassUseMode.Base);
        Message = null;
        Editor = new SkillRotationEditorViewModel(null, className, mode, SaveAsync);
    }

    [RelayCommand]
    private void Edit(SkillSetRow? row)
    {
        if (row is null)
            return;
        Message = null;
        try
        {
            Editor = new SkillRotationEditorViewModel(row.Set, null, null, SaveAsync);
        }
        catch (FormatException)
        {
            Message = $"This rotation cannot be opened in the editor: {row.Skills}";
        }
    }

    [RelayCommand]
    private void CloseEditor() => Editor = null;

    [RelayCommand]
    private void Delete(SkillSetRow? row) => PendingDelete = row;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private Task ConfirmDeleteAsync()
    {
        SkillSetRow? row = PendingDelete;
        PendingDelete = null;
        return row is null ? Task.CompletedTask : ChangeAsync(() => _store.Delete(row.Set.ClassName, row.Set.Mode), $"Deleted {row.Set.ClassName} ({row.Mode}).");
    }

    [RelayCommand]
    private Task RestoreAsync(SkillSetRow? row) => row is null
        ? Task.CompletedTask
        : ChangeAsync(() => _store.RestoreCommunity(row.Set.ClassName, row.Set.Mode), $"{row.Set.ClassName} ({row.Mode}) is the community version again.");

    [RelayCommand]
    private void AskReset() => ConfirmReset = true;

    [RelayCommand]
    private void CancelReset() => ConfirmReset = false;

    /// <summary>Replaces every rotation with the community file (the desktop's Reset Sets).</summary>
    [RelayCommand]
    private Task ResetAllAsync()
    {
        ConfirmReset = false;
        return ChangeAsync(_store.ResetAll, "All rotations are the community version again.");
    }

    private async Task SaveAsync(SkillRotationEditorViewModel editor)
    {
        editor.Error = null;
        SkillSet set = editor.ToSkillSet();
        SkillSet? original = editor.Original;
        // Skua finds a class's rotations ignoring case: a new rotation joins an existing class under its spelling.
        if (original is null && _all.FirstOrDefault(s => string.Equals(s.ClassName, set.ClassName, StringComparison.OrdinalIgnoreCase)) is SkillSet sameClass)
            set = set with { ClassName = sameClass.ClassName };
        bool moved = original is not null
            && (!string.Equals(original.ClassName, set.ClassName, StringComparison.Ordinal) || !string.Equals(original.Mode, set.Mode, StringComparison.OrdinalIgnoreCase));
        if ((original is null || moved) && _all.Any(s => string.Equals(s.ClassName, set.ClassName, StringComparison.Ordinal) && string.Equals(s.Mode, set.Mode, StringComparison.OrdinalIgnoreCase)))
        {
            editor.Error = $"{set.ClassName} already has a {set.Mode} rotation: open that one to change it.";
            return;
        }
        editor.IsSaving = true;
        try
        {
            await Task.Run(() =>
            {
                _store.Save(set, moved ? original!.ClassName : null, moved ? original!.Mode : null);
                ReloadSkua();
            });
            Editor = null;
            Message = $"Saved {set.ClassName} ({set.Mode}).";
            await ReloadListAsync(set.ClassName);
        }
        catch (EngineException e)
        {
            editor.Error = e.Message;
        }
        finally
        {
            editor.IsSaving = false;
        }
    }

    private async Task ChangeAsync(Action change, string done)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                change();
                ReloadSkua();
            });
            Message = done;
            await ReloadListAsync(SelectedClass?.ClassName);
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadListAsync(string? keepClass)
    {
        List<SkillSet> sets = await Task.Run(() => _store.Load().ToList());
        Show(sets);
        if (keepClass is not null)
            SelectedClass = Group(sets).FirstOrDefault(c => c.ClassName == keepClass);
    }

    private void Show(List<SkillSet> sets)
    {
        _all = sets;
        int classes = sets.Select(s => s.ClassName).Distinct(StringComparer.Ordinal).Count();
        int changed = sets.Count(s => s.Origin != SkillSetOrigin.Community);
        Summary = $"{classes} classes, {sets.Count} rotations" + (changed > 0 ? $", {changed} edited or yours" : string.Empty);
        Filter();
        if (SelectedClass is not null)
            SelectedClass = Group(sets).FirstOrDefault(c => c.ClassName == SelectedClass.ClassName);
    }

    private void Filter()
    {
        string search = SearchText.Trim();
        Classes.Clear();
        foreach (SkillClassItem item in Group(_all))
        {
            if (search.Length == 0 || item.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase))
                Classes.Add(item);
        }
    }

    private IEnumerable<SkillClassItem> Group(IEnumerable<SkillSet> sets) => sets
        .GroupBy(s => s.ClassName, StringComparer.Ordinal)
        .Select(g => new SkillClassItem(g.Key, g.Select(s => new SkillSetRow(s, this)).ToList(), this));

    // Skua's container reads the user file only when told; scripts read the container.
    private void ReloadSkua()
    {
        if (_host.Engine.SkuaServices?.GetService(typeof(IAdvancedSkillContainer)) is IAdvancedSkillContainer container)
            container.LoadSkills();
    }
}
