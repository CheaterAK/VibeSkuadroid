using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Droid.Core.Calendar;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;
using Skua.Core.Services;

namespace Skua.App.Droid.Tests;

/// <summary>The Calendar page: Skua's shared calendar rules and artix.com's calendar from a synthetic sample.</summary>
public class CalendarTests
{
    /// <summary>The sample's "now": Friday 20 Nov 2026, 10:00 server time (US Eastern, UTC-5).</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    internal static FakeHttp CalendarHttp()
    {
        FakeHttp http = new();
        string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "artix-calendar.sample.html"));
        HttpResponseMessage Page(string description) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><head><meta name=\"description\" content=\"{description}\"></head><body></body></html>"),
        };
        http.Routes["/calendar/"] = request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("www.artix.com", request.RequestUri.Host);
            Assert.Contains("VibeSkuffle", request.Headers.UserAgent.ToString());
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(sample) };
            r.Headers.Date = Now;
            return r;
        };
        http.Routes["/news/double-all-boost"] = _ => Page("Double ALL boost in AdventureQuest Worlds for 72 hours!");
        http.Routes["/news/aq3d-double-rep"] = _ => Page("Double Rep for 168 hours in AdventureQuest 3D");
        http.Routes["/news/weekend"] = _ => Page("This AQWorlds boost is available for a week &amp; more");
        http.Routes["/news/plain-http-page"] = _ => Page("Double CP in AQWorlds for 5 hours");
        http.Routes["/news/elsewhere"] = _ => Page("Double ALL in AQWorlds for 5 hours");
        return http;
    }

    [Fact]
    public void TheResetsAndTheParserAreTheDesktops()
    {
        DateTimeOffset thursday = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), AqwCalendar.NextDaily(thursday));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), AqwCalendar.NextWeekly(thursday));
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero), AqwCalendar.NextWeekly(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), AqwCalendar.PrevWeekly(Now));
        // Summer time: midnight Eastern is 04:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 4, 0, 0, TimeSpan.Zero), AqwCalendar.NextDaily(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("6d 3h 12m", AqwCalendar.FormatSpan(new TimeSpan(6, 3, 12, 9)));
        Assert.Equal("3h 12m 05s", AqwCalendar.FormatSpan(new TimeSpan(3, 12, 5)));
        Assert.Equal("1d 14h", AqwCalendar.FormatShort(TimeSpan.FromHours(38)));

        string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "artix-calendar.sample.html"));
        List<CalendarEvent> boosts = AqwCalendar.ParseBoosts(sample);
        Assert.Equal(6, boosts.Count); // Friday the 13th is no boost; the duplicate Double ALL is read once
        Assert.Equal("https://www.artix.com/news/weekend", boosts.Single(b => b.Title.StartsWith("Weekend", StringComparison.Ordinal)).Url);
        Assert.Equal(72, AqwCalendar.DefaultHours("Double ALL Boost 11.19.26"));
        Assert.Equal(48, AqwCalendar.DefaultHours("Double Gold Boost"));
        Assert.Null(AqwCalendar.DefaultHours("Weekend Boost"));
        Assert.Equal("Double ALL Boost", AqwCalendar.PlainTitle("Double ALL Boost 11.19.26"));
        Assert.True(AqwCalendar.IsOtherGame("Double Rep in AdventureQuest 3D"));
        Assert.False(AqwCalendar.IsOtherGame("AQWorlds and AQ3D"));
        Assert.True(AqwCalendar.IsArtixHttps(new Uri("https://www.artix.com/news/x")));
        Assert.False(AqwCalendar.IsArtixHttps(new Uri("http://www.artix.com/news/x")));
        Assert.False(AqwCalendar.IsArtixHttps(new Uri("https://example.com/news/x")));
        Assert.False(AqwCalendar.IsArtixHttps(new Uri("https://www.artix.com.example.com/x")));
        Assert.False(AqwCalendar.IsArtixHttps(new Uri("https://user@www.artix.com/x")));
    }

    [Fact]
    public async Task ThePageShowsTheResetsAndTheBoostsReadOverHttpsOnly()
    {
        await using TestApp app = new();
        FakeHttp http = CalendarHttp();
        CalendarViewModel page = new(app.Services with { Calendar = new ArtixCalendarSource(http) });
        await page.RefreshAsync();
        Assert.Equal(string.Empty, page.Note);
        Assert.StartsWith("Fri 20 Nov 10:0", page.ServerTime);
        Assert.StartsWith("13h 5", page.DailyText);
        Assert.StartsWith("6d 13h 5", page.WeeklyText);
        Assert.InRange(page.WeeklyProgress, 5, 7);

        Assert.Equal(["Double ALL Boost", "Double Class Points Boost"], page.ActiveBoosts.Select(b => b.Name));
        CalendarBoostRow all = page.ActiveBoosts[0];
        Assert.Equal("3 d  ·  starts Thu 19 Nov", all.Detail);
        Assert.StartsWith("1d 1", all.Status);
        Assert.InRange(all.ProgressPercent, 45, 48);
        Assert.Equal("2 d  ·  starts Fri 20 Nov", page.ActiveBoosts[1].Detail); // its page is plain http: not read, default length
        Assert.Equal(["Double Gold Boost", "Elsewhere Boost", "Weekend Boost"], page.UpcomingBoosts.Select(b => b.Name).Order());
        Assert.Equal("7 d  ·  starts Sat 21 Nov", page.UpcomingBoosts.Single(b => b.Name == "Weekend Boost").Detail);
        Assert.Equal("starts Sat 21 Nov", page.UpcomingBoosts.Single(b => b.Name == "Elsewhere Boost").Detail);
        Assert.DoesNotContain(page.ActiveBoosts.Concat(page.UpcomingBoosts), b => b.Name == "Double Rep Boost"); // another game

        lock (http.Requests)
        {
            Assert.Contains("/news/double-all-boost", http.Requests);
            Assert.DoesNotContain("/news/plain-http-page", http.Requests);
            Assert.DoesNotContain("/news/elsewhere", http.Requests);
            Assert.DoesNotContain("/news/double-gold", http.Requests); // starts after tomorrow
        }

        // The source fails: the last list stays, with a note.
        http.Routes.Remove("/calendar/");
        await page.RefreshAsync();
        Assert.Contains("showing the last list", page.Note);
        Assert.Equal(2, page.ActiveBoosts.Count);
    }

    [AvaloniaFact]
    public async Task TheCalendarPageRendersAndTicksOnlyWhileShown()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new();
        AppServices services = new(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths)
        {
            Calendar = new ArtixCalendarSource(CalendarHttp()),
        };
        using ShellViewModel shell = new(services);
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        shell.NavigateCommand.Execute("Settings");
        shell.Settings.OpenToolsCommand.Execute(null);
        ToolsViewModel tools = shell.Settings.Tools;
        CalendarViewModel page = shell.Settings.CalendarPages.Calendar;
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Calendar"));
        Assert.True(page.Visible);
        for (int i = 0; i < 300 && page.ActiveBoosts.Count == 0; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Assert.Equal(2, page.ActiveBoosts.Count);
        Render();
        Control view = window.GetVisualDescendantOfType(nameof(CalendarView))!;
        Assert.NotNull(view);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Double ALL Boost");
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Running now" && t.IsEffectivelyVisible);
        Assert.True(shell.GoBack());
        Assert.False(page.Visible);
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Calendar"));
        shell.SetUiVisible(false);
        Assert.False(page.Visible);
        shell.SetUiVisible(true);
        Assert.True(page.Visible);
        shell.NavigateCommand.Execute("Home");
        Assert.False(page.Visible);

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
