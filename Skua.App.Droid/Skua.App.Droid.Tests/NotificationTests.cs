using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

public class DropWatchListTests
{
    [Fact]
    public void InputIsSplitOnBarsTrimmedAndDeduplicated()
    {
        Assert.Equal(["Pixie Wing", "Blinding Light of Destiny"], DropWatchList.Parse("  Pixie Wing | Blinding Light of Destiny || "));
        Assert.Empty(DropWatchList.Parse("   "));
        IReadOnlyList<string> list = DropWatchList.Add(["Pixie Wing"], "pixie wing|Void Aura| Void Aura ");
        Assert.Equal(["Pixie Wing", "Void Aura"], list);
    }

    [Fact]
    public void MatchingIsByWholeNameIgnoringCaseAndSpaces()
    {
        string[] watched = ["Void Aura", "Pixie Wing"];
        Assert.True(DropWatchList.Matches(watched, "void aura"));
        Assert.True(DropWatchList.Matches(watched, " Pixie Wing "));
        Assert.False(DropWatchList.Matches(watched, "Void Aura Fragment"));
        Assert.False(DropWatchList.Matches(watched, "Aura"));
        Assert.False(DropWatchList.Matches(watched, null));
        Assert.False(DropWatchList.Matches([], "Void Aura"));
    }

    [Fact]
    public void SettingsKeepTheListAcrossInstances()
    {
        string path = Path.Combine(Sandbox.NewDir(), "notify.json");
        NotificationSettingsStore store = new(path);
        store.Update(s => s with { WatchedItems = DropWatchList.Add(s.WatchedItems, "A|B|a"), AutoRejectJunk = true });
        NotificationSettingsStore reread = new(path);
        Assert.Equal(["A", "B"], reread.Current.WatchedItems);
        Assert.True(reread.Current.AutoRejectJunk);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new NotificationSettingsStore(path).Current.WatchedItems);
    }
}

public class NotificationHistoryTests
{
    private static GameEvent E(GameEventKind kind, string? name = null, int id = 0) => new(kind, DateTimeOffset.UtcNow) { Name = name, Id = id };

    [Fact]
    public void EntriesReadLikeTheDesktopNotificationCenter()
    {
        Assert.Equal("Pixie Wing (dropped)", NotificationEntry.From(E(GameEventKind.ItemDropped, "Pixie Wing"))!.Message);
        Assert.Equal("Pixie Wing (picked up)", NotificationEntry.From(E(GameEventKind.ItemObtained, "Pixie Wing"))!.Message);
        Assert.Equal("Entered battleon", NotificationEntry.From(E(GameEventKind.MapChanged, "battleon"))!.Message);
        Assert.Equal("Completed quest #1000", NotificationEntry.From(E(GameEventKind.QuestCompleted, id: 1000))!.Message);
        Assert.Equal("Script started: Farm", NotificationEntry.From(E(GameEventKind.ScriptStarted, "Farm"))!.Message);
        Assert.Equal(NotificationKind.Warning, NotificationEntry.From(E(GameEventKind.Relogin))!.Kind);
        Assert.Equal(NotificationKind.System, NotificationEntry.From(E(GameEventKind.MapChanged, "x"))!.Kind);
        Assert.Equal("Sold 2 item(s) for 1,500 gold", NotificationEntry.From(new GameEvent(GameEventKind.ItemSold, DateTimeOffset.UtcNow) { Quantity = 2, Amount = 1500 })!.Message);
    }

    [Fact]
    public void HistoryIsBoundedNewestFirstAndSurvivesARestart()
    {
        string path = Path.Combine(Sandbox.NewDir(), "history.json");
        using (NotificationHistory history = new(path, saveDelay: TimeSpan.FromMinutes(5)))
        {
            for (int i = 0; i < NotificationHistory.Capacity + 20; i++)
                history.Add(new NotificationEntry(DateTimeOffset.UtcNow.AddSeconds(i), NotificationKind.Item, $"item {i}", Watched: i % 2 == 0));
            Assert.Equal(NotificationHistory.Capacity, history.Count);
            Assert.Equal($"item {NotificationHistory.Capacity + 19}", history.Entries[0].Message);
            Assert.False(File.Exists(path)); // the save waits for its timer...
            history.Flush();                 // ...or a flush
            Assert.True(File.Exists(path));
        }

        using NotificationHistory reread = new(path);
        Assert.Equal(NotificationHistory.Capacity, reread.Count);
        Assert.Equal($"item {NotificationHistory.Capacity + 19}", reread.Entries[0].Message);
        Assert.False(reread.Entries[0].Watched);
        Assert.True(reread.Entries[1].Watched);
        Assert.Equal(NotificationKind.Item, reread.Entries[0].Kind);
        Assert.Equal("item 20", reread.Entries[^1].Message);

        reread.Clear();
        reread.Flush();
        using NotificationHistory cleared = new(path);
        Assert.Equal(0, cleared.Count);
    }

    [Fact]
    public async Task TheTimerSavesAndADamagedFileStartsEmpty()
    {
        string path = Path.Combine(Sandbox.NewDir(), "history.json");
        using (NotificationHistory history = new(path, saveDelay: TimeSpan.FromMilliseconds(20)))
        {
            history.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.Activity, "Script started: A"));
            await Sandbox.Until(() => File.Exists(path));
        }
        File.WriteAllText(path, "[{\"broken\":");
        using NotificationHistory damaged = new(path);
        Assert.Equal(0, damaged.Count);
    }

    [Fact]
    public void MessagesAreScrubbedBeforeTheyAreKept()
    {
        Skua.App.Droid.Core.Security.SecretScrubber scrubber = new();
        scrubber.Add("hunter2-secret");
        string path = Path.Combine(Sandbox.NewDir(), "history.json");
        using NotificationHistory history = new(path, scrubber.Scrub);
        history.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.Error, "Script error (X): bad hunter2-secret"));
        history.Flush();
        Assert.DoesNotContain("hunter2-secret", File.ReadAllText(path));
        Assert.Contains("<redacted>", history.Entries[0].Message);
    }

    [Fact]
    public void TheCenterFiltersCountsAndOnlyUpdatesWhileShown()
    {
        using NotificationHistory history = new(null);
        NoPicker picker = new();
        using NotificationCenterViewModel center = new(history, new InlineDispatcher(), picker, new NullPlatformServices());
        history.Add(new NotificationEntry(DateTimeOffset.UtcNow.AddSeconds(-2), NotificationKind.Item, "Pixie Wing (dropped)"));
        Assert.Empty(center.FilteredNotifications); // hidden: no UI work

        center.IsActive = true;
        Assert.Single(center.FilteredNotifications);
        history.Add(new NotificationEntry(DateTimeOffset.UtcNow.AddSeconds(-1), NotificationKind.Warning, "Your character died."));
        history.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.System, "Entered battleon"));
        Assert.Equal(3, center.FilteredNotifications.Count);
        Assert.Equal("Entered battleon", center.FilteredNotifications[0].Message);
        Assert.Equal(3, center.Filters[0].Count);
        Assert.Equal(1, center.Filters.Single(f => f.Kind == NotificationKind.Warning).Count);

        center.SelectFilterCommand.Execute(center.Filters.Single(f => f.Kind == NotificationKind.Item));
        Assert.Equal(["Pixie Wing (dropped)"], center.FilteredNotifications.Select(e => e.Message));
        Assert.True(center.Filters.Single(f => f.Kind == NotificationKind.Item).IsSelected);
        center.SelectFilterCommand.Execute(center.Filters.Single(f => f.Kind == NotificationKind.Error));
        Assert.True(center.IsEmpty);
        Assert.Equal("No error events yet.", center.EmptyText);

        center.SelectFilterCommand.Execute(center.Filters[0]);
        center.CopyCommand.Execute(null);
        string[] lines = picker.Copied!.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("[Item] Pixie Wing (dropped)", lines[0]); // oldest first, the desktop's line format
        Assert.EndsWith("[Map] Entered battleon", lines[2]);

        center.ClearCommand.Execute(null);
        Assert.True(center.IsEmpty);
        Assert.Equal(0, center.Filters[0].Count);
    }
}
