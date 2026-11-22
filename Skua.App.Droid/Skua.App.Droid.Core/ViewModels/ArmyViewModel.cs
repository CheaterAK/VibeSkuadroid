using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Skills;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One status card of the Army page.</summary>
public sealed partial class ArmyCardViewModel : ObservableObject
{
    private readonly ArmyViewModel _page;

    public ArmyCardViewModel(ArmyViewModel page, ArmyMember member)
    {
        _page = page;
        Member = member;
        Update();
    }

    public ArmyMember Member { get; private set; }

    public string Id => Member.Account.Id;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _kind = string.Empty;

    [ObservableProperty]
    private string _connection = string.Empty;

    [ObservableProperty]
    private string _location = string.Empty;

    [ObservableProperty]
    private string _activity = string.Empty;

    [ObservableProperty]
    private string _vitals = string.Empty;

    [ObservableProperty]
    private string _memory = string.Empty;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string _role = string.Empty;

    public void Update(ArmyMember? member = null)
    {
        if (member is not null)
            Member = member;
        ArmyMember m = Member;
        EngineSnapshot s = m.Snapshot;
        CharacterState? c = s.Character;
        Title = c is null ? m.Account.Label : $"{c.Name} · lvl {c.Level}";
        Kind = m.KindText + (m.Account.TagList.Count > 0 ? " · " + string.Join(", ", m.Account.TagList) : string.Empty);
        IsOnline = s.IsOnline;
        Connection = s.Connection switch
        {
            ConnectionPhase.Online => s.ServerName ?? "Online",
            ConnectionPhase.Offline => "Offline",
            _ => s.StatusText ?? s.Connection.ToString(),
        };
        Location = c is null ? string.Empty : $"{c.Map} · {c.Cell} {c.Pad}";
        Vitals = c is null ? string.Empty : $"HP {c.Hp}/{c.HpMax} · MP {c.Mp}/{c.MpMax}{(c.Alive ? string.Empty : " · dead")}";
        Activity = m.Activity;
        Memory = m.IsLight ? "light session" : s.Memory.RssBytes > 0 ? $"{s.Memory.RssMb:0} MB" : string.Empty;
        Note = m.Note;
        string? master = _page.Coordinator.Accounts.Current.MasterId;
        Role = master == m.Account.Id ? "master" : (_page.Coordinator.Accounts.Current.FollowerIds ?? []).Contains(m.Account.Id) ? "follower" : string.Empty;
    }

    [RelayCommand]
    private Task Login() => _page.RunAsync($"Logging in {Member.Account.Label}", async () => [await _page.Coordinator.LoginAsync(Member)]);

    [RelayCommand]
    private Task Logout() => _page.RunAsync($"Logging out {Member.Account.Label}", async () => [await _page.Coordinator.LogoutAsync(Member)]);

    [RelayCommand]
    private void Edit() => _page.EditAccount(Member.Account);

    [RelayCommand]
    private void MakeMaster()
    {
        ArmyState s = _page.Coordinator.Accounts.Current;
        _page.Coordinator.SetRoster(Id, (s.FollowerIds ?? []).Where(f => f != Id));
        _page.Refresh();
    }

    [RelayCommand]
    private void ToggleFollower()
    {
        ArmyState s = _page.Coordinator.Accounts.Current;
        List<string> followers = (s.FollowerIds ?? []).ToList();
        if (!followers.Remove(Id))
            followers.Add(Id);
        _page.Coordinator.SetRoster(s.MasterId == Id ? null : s.MasterId, followers);
        _page.Refresh();
    }
}

/// <summary>
/// The Army page: the account manager, one status card per member, the army controls (Start All, Stop All, Log out
/// All, Load Script to All, Jump All to Map/Cell/Pad, Jump All to Player), light tasks for all, ButlerV5's army
/// (master, followers, summon, release, quest sync) and the memory line.
/// </summary>
public sealed partial class ArmyViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private volatile bool _visible = true;
    private int _refreshQueued;

    public ArmyViewModel(AppServices services, ArmyCoordinator coordinator)
    {
        _services = services;
        Coordinator = coordinator;
        Coordinator.Changed += OnChanged;
        _fullSlots = coordinator.Accounts.Current.FullSlots;
        Refresh();
    }

    public ArmyCoordinator Coordinator { get; }

    public ObservableCollection<ArmyCardViewModel> Cards { get; } = new();

    public IReadOnlyList<string> Modes { get; } = ["Light", "Full"];

    public IReadOnlyList<int> SlotChoices { get; } = Enumerable.Range(1, SlotIds.MaxFullSlots).ToList();

    /// <summary>The page is on screen (refreshes only then).</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _rosterText = string.Empty;

    [ObservableProperty]
    private int _fullSlots;

    [ObservableProperty]
    private bool _autoQuestSync;

    // Commands' inputs.
    [ObservableProperty]
    private string _jumpMap = string.Empty;

    [ObservableProperty]
    private string _jumpCell = "Enter";

    [ObservableProperty]
    private string _jumpPad = "Spawn";

    [ObservableProperty]
    private string _jumpPlayer = string.Empty;

    [ObservableProperty]
    private string _scriptPath = string.Empty;

    [ObservableProperty]
    private bool _profilePerAccount;

    [ObservableProperty]
    private string _fightMonsters = "*";

    [ObservableProperty]
    private string _questId = string.Empty;

    [ObservableProperty]
    private string _questLimit = "10";

    [ObservableProperty]
    private string _pickupList = string.Empty;

    [ObservableProperty]
    private string _junkList = string.Empty;

    [ObservableProperty]
    private bool _rejectOthers;

    // Account editor.
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string? _editId;

    [ObservableProperty]
    private string _editUsername = string.Empty;

    [ObservableProperty]
    private string _editPassword = string.Empty;

    [ObservableProperty]
    private string _editDisplayName = string.Empty;

    [ObservableProperty]
    private string _editServer = string.Empty;

    [ObservableProperty]
    private string _editTags = string.Empty;

    [ObservableProperty]
    private string _editMode = "Light";

    [ObservableProperty]
    private int _editSlot = 2;

    [ObservableProperty]
    private string? _editError;

    [ObservableProperty]
    private string _editPasswordHint = "Password";

    public bool IsEmpty => Cards.Count == 0;

    public void SetUiVisible(bool visible)
    {
        _visible = visible;
        if (visible)
            Refresh();
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            Refresh();
    }

    partial void OnFullSlotsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, SlotIds.MaxFullSlots);
        if (Coordinator.Accounts.Current.FullSlots != clamped)
            Coordinator.Accounts.Update(s => s with { FullSlots = clamped });
    }

    partial void OnAutoQuestSyncChanged(bool value) => Coordinator.AutoQuestSync = value;

    private void OnChanged(object? sender, EventArgs e)
    {
        if (!_visible || !IsActive)
            return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _services.Dispatcher.Post(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        });
    }

    /// <summary>Rebuilds the cards from the members (on the UI thread).</summary>
    public void Refresh()
    {
        IReadOnlyList<ArmyMember> members = Coordinator.Members;
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!members.Any(m => m.Account.Id == Cards[i].Id))
                Cards.RemoveAt(i);
        }
        for (int i = 0; i < members.Count; i++)
        {
            ArmyCardViewModel? card = Cards.FirstOrDefault(c => c.Id == members[i].Account.Id);
            if (card is null)
                Cards.Insert(Math.Min(i, Cards.Count), new ArmyCardViewModel(this, members[i]));
            else
                card.Update(members[i]);
        }
        OnPropertyChanged(nameof(IsEmpty));
        var use = Coordinator.MemoryUse();
        Army.DeviceMemory device = DeviceMemory();
        string slots = use.Slots.Count == 0 ? string.Empty : " · " + string.Join(" · ", use.Slots.Select(s => $"slot {s.Slot} {(s.Bytes > 0 ? MemoryAdmission.Mb(s.Bytes) : "?")}"));
        string free = device.IsKnown ? $" · device {MemoryAdmission.Mb(device.AvailableBytes)} free" : string.Empty;
        string room = device.IsKnown
            ? $"Room for {MemoryAdmission.HowManyMore(true, device, Coordinator.Estimates, Cards.Count(c => !c.Member.IsLight && c.IsOnline), Coordinator.Accounts.Current.FullSlots)} more full slot(s), about {Math.Min(99, MemoryAdmission.HowManyMore(false, device, Coordinator.Estimates, 0, 0))} light."
            : string.Empty;
        MemoryText = $"Main {(use.MainBytes > 0 ? MemoryAdmission.Mb(use.MainBytes) : "?")} (slot 1 + {use.Lights} light){slots} · total {MemoryAdmission.Mb(use.TotalBytes)}{free}. {room}";
        ArmyMember? master = Coordinator.Master;
        RosterText = master is null
            ? "No master: tap Master on a card, then Follower on the others."
            : $"Master {master.Name}; followers: {string.Join(", ", Coordinator.Followers.Select(f => f.Name))}";
    }

    private Army.DeviceMemory DeviceMemory()
    {
        try
        {
            return PlatformMemory?.Invoke() ?? Army.DeviceMemory.Unknown;
        }
        catch (Exception)
        {
            return Army.DeviceMemory.Unknown;
        }
    }

    /// <summary>Reads the device's memory (set by the platform head).</summary>
    public Func<Army.DeviceMemory>? PlatformMemory { get; set; }

    internal async Task RunAsync(string what, Func<Task<IReadOnlyList<ArmyResult>>> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = what + "...";
        try
        {
            IReadOnlyList<ArmyResult> results = await action();
            StatusText = results.Count == 0
                ? $"{what}: nothing to do."
                : $"{what}: {results.Count(r => r.Ok)}/{results.Count} done." + string.Concat(results.Where(r => !r.Ok).Take(4).Select(r => $"\n{r.Member}: {r.Message}"));
        }
        catch (EngineException e)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private Task StartAll() => RunAsync("Start All", () => Coordinator.StartAllAsync());

    [RelayCommand]
    private Task StopAll() => RunAsync("Stop All", () => Coordinator.StopAllAsync());

    [RelayCommand]
    private Task LogoutAll() => RunAsync("Log out all", () => Coordinator.LogoutAllAsync());

    [RelayCommand]
    private Task LoadScriptToAll()
    {
        string path = ScriptPath.Trim().Replace('\\', '/');
        if (path.Length == 0)
        {
            StatusText = "Enter the script's path in the script folder (as the Scripts page shows it), e.g. Story/Doom.cs.";
            return Task.CompletedTask;
        }
        string full = Path.IsPathRooted(path) ? path : Path.Combine(_services.Paths.ScriptsDir, path);
        if (!File.Exists(full))
        {
            StatusText = $"No script {path} in the script folder.";
            return Task.CompletedTask;
        }
        bool perAccount = ProfilePerAccount;
        string script = Path.GetFileNameWithoutExtension(full);
        return RunAsync($"Load {script} to all", () => Coordinator.LoadScriptToAllAsync(full,
            m => perAccount ? new ScriptStartOptions($"Army_{script}_{m.Account.Id}", Silent: true) : ScriptStartOptions.Default));
    }

    [RelayCommand]
    private Task JumpAll()
    {
        string map = JumpMap.Trim();
        return RunAsync($"Jump all to {map}", () => Coordinator.JumpAllAsync(map, string.IsNullOrWhiteSpace(JumpCell) ? "Enter" : JumpCell.Trim(), string.IsNullOrWhiteSpace(JumpPad) ? "Spawn" : JumpPad.Trim()));
    }

    [RelayCommand]
    private Task JumpAllToPlayer()
    {
        string name = JumpPlayer.Trim();
        return RunAsync($"Jump all to {name}", () => Coordinator.JumpAllToPlayerAsync(name));
    }

    [RelayCommand]
    private Task FightAll()
    {
        string monsters = FightMonsters;
        SkillSetStore skills = new(_services.Paths.SkuaDir);
        IReadOnlyList<SkillSet> sets;
        try
        {
            sets = skills.Load();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            sets = [];
        }
        // Each light member fights with its class's "Base" rotation from Advanced Skills when there is one.
        LightRotation RotationFor(ArmyMember m)
        {
            string? cls = m.Snapshot.Character?.ClassName;
            SkillSet? set = cls is null ? null : sets.FirstOrDefault(s => string.Equals(s.ClassName, cls, StringComparison.OrdinalIgnoreCase) && s.Mode == "Base");
            return set is null ? LightRotation.Default : LightRotation.Parse(set.Skills, set.UseMode.ToString(), set.Timeout);
        }
        return RunAsync($"Fight {monsters}", () => Task.FromResult(Coordinator.FightAll(monsters, RotationFor)));
    }

    [RelayCommand]
    private Task QuestAll()
    {
        if (!int.TryParse(QuestId.Trim(), out int id) || id <= 0)
        {
            StatusText = "Enter a quest ID.";
            return Task.CompletedTask;
        }
        int limit = int.TryParse(QuestLimit.Trim(), out int l) ? Math.Max(0, l) : 0;
        return RunAsync($"Quest {id}", () => Task.FromResult(Coordinator.QuestAll(id, limit)));
    }

    [RelayCommand]
    private void ApplyPickup()
    {
        static List<string> Split(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100).ToList();
        Coordinator.SetPickupAll(new LightPickup(Split(PickupList), Split(JunkList), RejectOthers));
        StatusText = "Drop rules applied to the light accounts.";
    }

    [RelayCommand]
    private Task SummonAll() => RunAsync("Summon all", () => Coordinator.SummonAllAsync());

    [RelayCommand]
    private Task ReleaseAll() => RunAsync("Release", () => Task.FromResult(Coordinator.ReleaseAll()));

    [RelayCommand]
    private Task SyncQuests() => RunAsync("Quest sync", () => Coordinator.SyncQuestsAsync());

    // ------------------------------------------------------------------ account manager

    [RelayCommand]
    private void AddAccount() => EditAccount(null);

    public void EditAccount(ArmyAccount? account)
    {
        EditId = account?.Id;
        EditUsername = account?.Username ?? string.Empty;
        EditPassword = string.Empty;
        EditDisplayName = account?.DisplayName ?? string.Empty;
        EditServer = account?.Server ?? string.Empty;
        EditTags = string.Join(", ", account?.TagList ?? []);
        EditMode = account?.Mode == AccountMode.Full ? "Full" : "Light";
        EditSlot = account?.FullSlot ?? 2;
        EditError = null;
        EditPasswordHint = account is not null && Coordinator.HasPassword(account.Id) ? "Saved password (leave empty to keep it)" : "Password";
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditPassword = string.Empty;
        IsEditing = false;
    }

    [RelayCommand]
    private void SaveAccount()
    {
        try
        {
            ArmyAccount account = new(
                EditId ?? AccountStore.NewId(),
                EditUsername.Trim(),
                string.IsNullOrWhiteSpace(EditDisplayName) ? null : EditDisplayName.Trim(),
                string.IsNullOrWhiteSpace(EditServer) ? null : EditServer.Trim(),
                EditTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                EditMode == "Full" ? AccountMode.Full : AccountMode.Light,
                EditMode == "Full" ? EditSlot : null);
            if (EditPassword.Length == 0 && !Coordinator.HasPassword(account.Id))
                throw new ArgumentException("Enter the password (it is kept encrypted by the Android Keystore).");
            Coordinator.SaveAccount(account, EditPassword.Length > 0 ? EditPassword : null);
            EditPassword = string.Empty;
            IsEditing = false;
            Refresh();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            EditError = e.Message;
        }
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        if (EditId is string id)
            Coordinator.RemoveAccount(id);
        EditPassword = string.Empty;
        IsEditing = false;
        Refresh();
    }

    /// <summary>System back: closes the editor first.</summary>
    public bool CloseOverlay()
    {
        if (!IsEditing)
            return false;
        CancelEdit();
        return true;
    }

    public void Dispose() => Coordinator.Changed -= OnChanged;
}
