using System.Text.Json;
using RuffleSpike.Core;

namespace RuffleSpike.Tests;

public class RecorderTests
{
    private double _t;
    private SpikeRecorder New(string? secret = null) => new(() => _t, secret);

    [Fact]
    public void Page_records_become_milestones_samples_and_events()
    {
        SpikeRecorder r = New();
        _t = 1.5; Assert.True(r.Console("[vspike] {\"t\":10,\"type\":\"mark\",\"name\":\"first_frame\"}", "LOG"));
        _t = 2.0; r.Console("[vspike] {\"t\":20,\"type\":\"mark\",\"name\":\"first_frame\"}", "LOG"); // repeats ignored
        _t = 6.0; r.Console("[vspike] {\"t\":30,\"type\":\"mark\",\"name\":\"login_screen\",\"label\":\"\\\"Init\\\"\"}", "LOG");
        _t = 7.0; r.Console("[vspike] {\"t\":40,\"type\":\"fps\",\"fps\":4,\"raf\":50,\"longTaskPct\":20,\"packetsPerSec\":1}", "LOG");
        _t = 8.0; r.Console("[vspike] {\"t\":50,\"type\":\"mem\",\"wasmMB\":81.4,\"jsHeapMB\":19.6}", "LOG");
        _t = 9.0; r.Console("[vspike] {\"t\":60,\"type\":\"socket_state\",\"connected\":false}", "LOG");
        r.NativeMemory(new NativeMemorySample(300, 400, 20, 30, 5000, false));

        Assert.Equal(1.5, r.MarkTime("first_frame"));
        Assert.Equal(6.0, r.MarkTime("login_screen"));
        string report = r.BuildReport();
        Assert.Contains("first_frame             1.5", report);
        Assert.Contains("login_screen            6.0", report);
        Assert.Contains("whole run: n=1 avg 4.00", report);
        Assert.Contains("wasm 81", report);
        Assert.Contains("socket_state connected=false", report);
        Assert.Contains("login", r.Hud());
    }

    [Fact]
    public void Console_errors_are_scrubbed_counted_and_deduplicated()
    {
        const string secret = "abcdefghijklmnopqrstuvwxyz0123456789-_ABCDE";
        SpikeRecorder r = New(secret);
        Assert.False(r.Console($"WebSocket connection to 'ws://127.0.0.1:4000/{secret}/sock7.aq.com/5593' failed", "ERROR"));
        r.Console($"WebSocket connection to 'ws://127.0.0.1:4000/{secret}/sock7.aq.com/5593' failed", "ERROR");
        r.Console("login failed pass=hunter2 token=abc123 user=Someone", "WARNING");
        r.Console("just a log line", "LOG");
        string report = r.BuildReport();
        Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain("hunter2", report);
        Assert.DoesNotContain("abc123", report);
        Assert.DoesNotContain("Someone", report);
        Assert.Contains("x2", report);
        Assert.Contains("Console errors/warnings: 3", report);
        Assert.DoesNotContain("just a log line", report);
    }

    [Fact]
    public void Renderer_crashes_and_blocked_requests_are_reported()
    {
        SpikeRecorder r = New();
        _t = 17;
        r.RendererGone(true, 2, "important");
        r.Blocked("https://www.example.com/a");
        r.Blocked("https://www.example.com/a");
        string report = r.BuildReport();
        Assert.Contains("renderer process gone: 1", report);
        Assert.Contains("didCrash=True rendererPriorityAtExit=2 (important)", report);
        Assert.Contains("x2 https://www.example.com/a", report);
    }

    [Fact]
    public void Bad_page_records_do_not_throw()
    {
        SpikeRecorder r = New();
        Assert.True(r.Console("[vspike] {not json", "LOG"));
        Assert.Contains("bad_page_record", r.BuildReport());
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_and_round_trip()
    {
        SpikeSettings s = SpikeSettings.FromJson(null);
        Assert.Equal(4, s.FrameRate);
        Assert.Equal("low", s.Quality);
        Assert.Equal("skua", s.Swf);
        s.FrameRate = 8; s.Renderer = "webgl"; s.SoakMinutes = 10;
        SpikeSettings back = SpikeSettings.FromJson(s.ToJson());
        Assert.Equal(8, back.FrameRate);
        Assert.Equal("webgl", back.Renderer);
        Assert.Equal(10, back.SoakMinutes);
    }

    [Fact]
    public void Out_of_range_values_fall_back()
    {
        SpikeSettings s = SpikeSettings.FromJson("{\"FrameRate\":60,\"Renderer\":\"vulkan\",\"Swf\":\"http://x\",\"SoakMinutes\":3,\"Ruffle\":\"../x\"}");
        Assert.Equal(4, s.FrameRate);
        Assert.Equal("auto", s.Renderer);
        Assert.Equal("skua", s.Swf);
        Assert.Equal(0, s.SoakMinutes);
        Assert.Equal("stable", s.Ruffle);
        Assert.Equal(4, SpikeSettings.FromJson("{broken").FrameRate);
    }

    [Fact]
    public void Page_config_lists_one_proxy_per_endpoint()
    {
        SpikeSettings s = new();
        string json = s.PageConfigJson([(new GameEndpoint("sock7.aq.com", 5593), "ws://127.0.0.1:1/s/sock7.aq.com/5593")]);
        using JsonDocument d = JsonDocument.Parse(json);
        JsonElement p = d.RootElement.GetProperty("socketProxy")[0];
        Assert.Equal("sock7.aq.com", p.GetProperty("host").GetString());
        Assert.Equal(5593, p.GetProperty("port").GetInt32());
        Assert.Equal(4, d.RootElement.GetProperty("frameRate").GetInt32());
        Assert.True(d.RootElement.GetProperty("modules").GetProperty("hidePlayers").GetBoolean());
    }
}
