using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Plugins.Shared.DailyTracker;

namespace Skua.Plugin.DailyTracker;

public partial class DailyTrackerWindow : Window
{
    private readonly IScriptInterface _bot;
    private CancellationTokenSource? _cts;
    private Timer? _refreshTimer;
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private List<QuestCategory> _categories = [];
    private bool _isRefreshing;

    public DailyTrackerWindow() : this(null!)
    {
    }

    public DailyTrackerWindow(IScriptInterface bot)
    {
        InitializeComponent();
        _bot = bot;
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_bot == null)
            return;

        _cts = new CancellationTokenSource();
        try
        {
            await LoadQuestsAsync();
            RegisterEventHandlers();
            StartLiveUpdates();
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"DailyTracker InitializeAsync error: {ex}");
        }
    }

    private void EnsureAllQuestsLoaded() => DailyQuestStatusReader.EnsureAllQuestsLoaded(_bot);

    private async Task LoadQuestsAsync()
    {
        int[] trackedQuests = DailyQuestCatalog.TrackedQuests;
        bool[] doneStates = await Task.Run(() =>
        {
            EnsureAllQuestsLoaded();
            bool[] results = new bool[trackedQuests.Length];
            for (int i = 0; i < trackedQuests.Length; i++)
                results[i] = DailyQuestStatusReader.TryReadState(_bot, trackedQuests[i], out bool done, out _) && done;
            return results;
        });

        List<QuestItem> questItems = [];
        for (int i = 0; i < trackedQuests.Length; i++)
        {
            QuestItem item = new(trackedQuests[i], DailyQuestCatalog.QuestNames[i], doneStates[i]);
            if (DailyQuestCatalog.QuestScriptNames.TryGetValue(trackedQuests[i], out string? script))
                item.ScriptName = script;
            questItems.Add(item);
        }

        _categories = [.. DailyQuestCatalog.Categories.Select(c => new QuestCategory { Name = c.Name, Quests = [.. questItems.Skip(c.Start).Take(c.Count)] })];

        Dispatcher.UIThread.Post(() => CategoriesControl.ItemsSource = _categories);
    }

    private void RegisterEventHandlers()
    {
        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, QuestTurninMessage, int>(
            this, (int)MessageChannels.GameEvents, (r, m) => r.OnQuestTurnIn(m));

        StrongReferenceMessenger.Default.Register<DailyTrackerWindow, ScriptStoppedMessage, int>(
            this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptStopped(m));
    }

    private void OnQuestTurnIn(QuestTurninMessage message)
    {
        int questId = message.QuestID;
        foreach (QuestCategory cat in _categories)
        {
            QuestItem? item = cat.Quests.Find(q => q.ID == questId);
            if (item != null)
            {
                Dispatcher.UIThread.Post(() => item.IsDone = true);
                break;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private void OnScriptStopped(ScriptStoppedMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch { }
        });
    }

    private async Task RefreshQuestStatesAsync()
    {
        if (_bot == null)
            return;

        if (!await _refreshLock.WaitAsync(0))
            return;

        try
        {
            await Task.Run(() =>
            {
                EnsureAllQuestsLoaded();
                foreach (QuestCategory category in _categories)
                {
                    foreach (QuestItem quest in category.Quests)
                    {
                        if (!DailyQuestStatusReader.TryReadState(_bot, quest.ID, out bool newState, out Exception? error))
                        {
                            System.Diagnostics.Trace.WriteLine($"Quest refresh failed {quest.ID}: {error}");
                            continue;
                        }

                        if (quest.IsDone != newState)
                            Dispatcher.UIThread.Post(() => quest.IsDone = newState);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"RefreshQuestStatesAsync error: {ex}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void StartLiveUpdates()
    {
        _refreshTimer = new Timer(async _ =>
        {
            try
            {
                if (_cts?.IsCancellationRequested != true)
                    await RefreshQuestStatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"DailyTracker live update failed: {ex}");
            }
        }, null, _refreshInterval, _refreshInterval);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        RefreshBtn.IsEnabled = false;
        if (RefreshText != null)
            RefreshText.Text = "Checking...";

        try
        {
            await RefreshQuestStatesAsync();
        }
        finally
        {
            if (RefreshText != null)
                RefreshText.Text = "Refresh";
            RefreshBtn.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private void StartScript_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string scriptName || string.IsNullOrWhiteSpace(scriptName)) return;
        StartScript(scriptName);
    }

    private void StartCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (DailyQuestCatalog.CategoryScript(btn.Tag as string ?? string.Empty) is string script)
            StartScript(script);
    }

    private void StartScript(string scriptName)
    {
        string path = Path.Combine(ClientFileSources.SkuaScriptsDIR, scriptName);
        if (!File.Exists(path))
        {
            System.Diagnostics.Trace.WriteLine($"Script not found: {path}");
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                _bot.Manager.SetLoadedScript(path);
                await _bot.Manager.StartScript();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to start script {scriptName}: {ex}");
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
        }
        catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}

public class QuestCategory : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCategoryScript)); }
    }

    public List<QuestItem> Quests { get; set; } = [];
    public bool HasCategoryScript => DailyQuestCatalog.HasCategoryScript(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class QuestItem : INotifyPropertyChanged
{
    private static readonly IBrush _completedBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush _incompleteBrush = new SolidColorBrush(Color.Parse("#C62828"));

    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;

    private bool _isDone;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            _isDone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string _scriptName = string.Empty;

    public string ScriptName
    {
        get => _scriptName;
        set
        {
            _scriptName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScript));
        }
    }

    public bool HasScript => !string.IsNullOrEmpty(ScriptName);
    public bool ShowScript => HasScript && !IsDone && !DailyQuestCatalog.IsLordOfOrder(ID);
    public bool IsLordOfOrder => DailyQuestCatalog.IsLordOfOrder(ID);
    public IBrush StatusBrush => IsDone ? _completedBrush : _incompleteBrush;
    public string StatusText => IsDone ? "Completed" : "Incomplete";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public QuestItem(int id, string name, bool isDone)
    {
        ID = id;
        Name = name;
        IsDone = isDone;
    }
}
