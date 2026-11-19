using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuffleSpike.Core;

/// <summary>The spike's settings (stored by the app as JSON; no credentials, ever).</summary>
public sealed class SpikeSettings
{
    public static readonly int[] FrameRates = [2, 4, 8, 24];
    public static readonly string[] Qualities = ["low", "medium"];
    public static readonly string[] Renderers = ["auto", "webgl", "wgpu-webgl", "webgpu", "canvas"];
    public static readonly string[] Swfs = ["skua", "official"];
    public static readonly string[] RuffleBuilds = ["stable", "nightly"];
    public static readonly int[] MaxExecutionDurations = [15, 60];
    public static readonly string[] Transports = ["channel", "loopback"];
    public static readonly string[] PixelRatios = ["native", "2", "1.5", "1"];

    /// <summary>"skua": this repo's skua.swf loader (ExternalInterface, modules); "official": the site's Loader4.swf.</summary>
    public string Swf { get; set; } = "skua";
    /// <summary>"stable": Ruffle 0.7.1; "nightly": 0.8.0-nightly.2026.11.19.</summary>
    public string Ruffle { get; set; } = "stable";
    /// <summary>
    /// Socket transport: "channel" (WebMessagePort to the app, no listening socket) or "loopback" (WebSocket bridge
    /// on 127.0.0.1 with a path secret; may be refused by Local Network Access checks).
    /// </summary>
    public string Transport { get; set; } = "channel";
    public int FrameRate { get; set; } = 4;
    public string Quality { get; set; } = "low";
    /// <summary>
    /// Caps the device pixel ratio Ruffle renders at ("native" = no cap). Ruffle sizes its canvas as CSS size x
    /// devicePixelRatio, so on a 2400x1080 screen (ratio 2.625) "1" draws about 7 times fewer pixels than native.
    /// </summary>
    public string PixelRatio { get; set; } = "2";
    public string Renderer { get; set; } = "auto";
    public int MaxExecutionDuration { get; set; } = 60;
    public bool HidePlayers { get; set; } = true;
    public bool DisableFX { get; set; } = true;
    /// <summary>skua.swf's killLag: hides the whole world (map, monsters, players); only the UI stays visible.</summary>
    public bool KillLag { get; set; }
    public bool SkipCutscenes { get; set; }
    public bool Mute { get; set; } = true;
    /// <summary>0 = off; otherwise records for this many minutes after reaching the game and then summarises.</summary>
    public int SoakMinutes { get; set; }
    public bool ShowHud { get; set; } = true;
    /// <summary>Fallback only: lets the https page open ws://127.0.0.1 even if the WebView treats it as mixed content.</summary>
    public bool AllowMixedContent { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses stored settings; unknown or out-of-range values fall back to the defaults.</summary>
    public static SpikeSettings FromJson(string? json)
    {
        SpikeSettings s;
        try { s = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<SpikeSettings>(json, Json) ?? new(); }
        catch (JsonException) { s = new(); }
        return s.Normalized();
    }

    public SpikeSettings Normalized()
    {
        SpikeSettings d = new();
        if (!Swfs.Contains(Swf)) Swf = d.Swf;
        if (!RuffleBuilds.Contains(Ruffle)) Ruffle = d.Ruffle;
        if (!Transports.Contains(Transport)) Transport = d.Transport;
        if (!FrameRates.Contains(FrameRate)) FrameRate = d.FrameRate;
        if (!Qualities.Contains(Quality)) Quality = d.Quality;
        if (!PixelRatios.Contains(PixelRatio)) PixelRatio = d.PixelRatio;
        if (!Renderers.Contains(Renderer)) Renderer = d.Renderer;
        if (!MaxExecutionDurations.Contains(MaxExecutionDuration)) MaxExecutionDuration = d.MaxExecutionDuration;
        if (SoakMinutes is not (0 or 10)) SoakMinutes = 0;
        return this;
    }

    public string Describe() =>
        $"loader={Swf} ruffle={Ruffle} transport={Transport} fps={FrameRate} quality={Quality} pixelRatio={PixelRatio} renderer={Renderer} maxExec={MaxExecutionDuration}s "
        + $"hidePlayers={On(HidePlayers)} disableFX={On(DisableFX)} killLag={On(KillLag)} skipCutscenes={On(SkipCutscenes)} "
        + $"mute={On(Mute)} soak={(SoakMinutes > 0 ? SoakMinutes + "min" : "off")} mixedContent={On(AllowMixedContent)}";

    private static string On(bool b) => b ? "on" : "off";

    /// <summary>The page's config.json: settings plus one socketProxy entry per allowlisted endpoint.</summary>
    public string PageConfigJson(IEnumerable<(GameEndpoint Endpoint, string ProxyUrl)> proxies)
    {
        JsonArray socketProxy = [];
        foreach ((GameEndpoint e, string url) in proxies)
            socketProxy.Add(new JsonObject { ["host"] = e.Host, ["port"] = e.Port, ["proxyUrl"] = url });
        JsonObject o = new()
        {
            ["swf"] = Swf,
            ["ruffle"] = Ruffle,
            ["transport"] = Transport,
            ["frameRate"] = FrameRate,
            ["quality"] = Quality,
            ["pixelRatio"] = PixelRatio,
            ["renderer"] = Renderer,
            ["maxExecutionDuration"] = MaxExecutionDuration,
            ["logLevel"] = "warn",
            ["mute"] = Mute,
            ["soakMinutes"] = SoakMinutes,
            ["modules"] = new JsonObject
            {
                ["hidePlayers"] = HidePlayers,
                ["disableFX"] = DisableFX,
                ["killLag"] = KillLag,
                ["skipCutscenes"] = SkipCutscenes,
            },
            ["socketProxy"] = socketProxy,
        };
        return o.ToJsonString();
    }
}
