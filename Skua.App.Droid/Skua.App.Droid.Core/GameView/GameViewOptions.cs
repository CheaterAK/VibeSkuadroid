using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.GameView;

/// <summary>
/// How the game view renders (Ruffle settings and skua.swf's load reducers) and its memory limit. Stored in the app
/// settings (main process) and handed to the game view process when it connects. Never holds a secret.
/// </summary>
/// <remarks>
/// Defaults from the spike's measurements on a Galaxy S21 Ultra (Android 13, Mali GPU, WebView 159, Ruffle 0.7.1,
/// channel transport, 4 fps, quality low): renderer webgl at pixel ratio 1 ran at 5.2 fps with the main thread ~8%
/// busy and the app's PSS stable at ~820 MB; renderer auto (wgpu-webgl) at ratio 2 went from ~600 MB to 2.7 GB within
/// 20 s of entering the game; canvas at ratio 1 managed 3.3 fps with the main thread ~96% busy.
/// </remarks>
public sealed record GameViewOptions
{
    public static readonly int[] FrameRates = [2, 4, 8];
    public static readonly string[] Qualities = ["low", "medium"];
    public static readonly string[] Renderers = ["webgl", "canvas", "wgpu-webgl", "auto"];
    public static readonly string[] PixelRatios = ["1", "1.5", "2", "native"];

    public const int MinMemoryLimitMb = 512;
    public const int MaxMemoryLimitMb = 4096;
    public const int DefaultMemoryLimitMb = 1536;

    /// <summary>Ruffle's frame rate lock (frames per second).</summary>
    public int FrameRate { get; init; } = 4;

    /// <summary>Stage quality.</summary>
    public string Quality { get; init; } = "low";

    /// <summary>Ruffle's renderer ("auto" lets Ruffle pick: wgpu-webgl on the measured phone, the worst choice there).</summary>
    public string Renderer { get; init; } = "webgl";

    /// <summary>
    /// Caps the device pixel ratio Ruffle renders at ("native" = no cap). Ruffle sizes its canvas as CSS size x
    /// devicePixelRatio, so on a 2400x1080 screen (ratio 2.625) "1" draws about 7 times fewer pixels than native.
    /// </summary>
    public string PixelRatio { get; init; } = "1";

    /// <summary>skua.swf's HidePlayers module (other players are not drawn).</summary>
    public bool HidePlayers { get; init; } = true;

    /// <summary>skua.swf's DisableFX module (skill effects are not drawn).</summary>
    public bool DisableFX { get; init; } = true;

    /// <summary>skua.swf's killLag: hides the whole world (map, monsters, players); only the game's UI stays visible.</summary>
    public bool KillLag { get; init; }

    /// <summary>Game sound off.</summary>
    public bool Mute { get; init; } = true;

    /// <summary>The measurement overlay (fps, main-thread load, memory).</summary>
    public bool ShowHud { get; init; }

    /// <summary>The game view process's PSS (MB) above which the view is closed and the app returns to headless.</summary>
    public int MemoryLimitMb { get; init; } = DefaultMemoryLimitMb;

    private static readonly JsonSerializerOptions s_json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Unknown or out-of-range values fall back to the defaults.</summary>
    public GameViewOptions Normalized()
    {
        GameViewOptions d = new();
        return this with
        {
            FrameRate = FrameRates.Contains(FrameRate) ? FrameRate : d.FrameRate,
            Quality = Qualities.Contains(Quality) ? Quality : d.Quality,
            Renderer = Renderers.Contains(Renderer) ? Renderer : d.Renderer,
            PixelRatio = PixelRatios.Contains(PixelRatio) ? PixelRatio : d.PixelRatio,
            MemoryLimitMb = Math.Clamp(MemoryLimitMb, MinMemoryLimitMb, MaxMemoryLimitMb),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static GameViewOptions FromJson(string? json)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GameViewOptions>(json, s_json))?.Normalized() ?? new GameViewOptions();
        }
        catch (JsonException)
        {
            return new GameViewOptions();
        }
    }

    /// <summary>One line for the report and the Console.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"renderer={Renderer} pixelRatio={PixelRatio} fps={FrameRate} quality={Quality} hidePlayers={On(HidePlayers)} disableFX={On(DisableFX)} killLag={On(KillLag)} mute={On(Mute)} hud={On(ShowHud)} memoryLimit={MemoryLimitMb}MB");

    private static string On(bool b) => b ? "on" : "off";

    /// <summary>The page's config.json: these settings plus one socketProxy entry per allowlisted endpoint. No secrets.</summary>
    public string PageConfigJson(IEnumerable<(GameEndpoint Endpoint, string ProxyUrl)> proxies)
    {
        JsonArray socketProxy = [];
        foreach ((GameEndpoint e, string url) in proxies)
            socketProxy.Add(new JsonObject { ["host"] = e.Host, ["port"] = e.Port, ["proxyUrl"] = url });
        JsonObject o = new()
        {
            ["frameRate"] = FrameRate,
            ["quality"] = Quality,
            ["pixelRatio"] = PixelRatio,
            ["renderer"] = Renderer,
            ["maxExecutionDuration"] = 60,
            ["logLevel"] = "warn",
            ["mute"] = Mute,
            ["modules"] = new JsonObject
            {
                ["hidePlayers"] = HidePlayers,
                ["disableFX"] = DisableFX,
                ["killLag"] = KillLag,
            },
            ["socketProxy"] = socketProxy,
        };
        return o.ToJsonString();
    }
}
