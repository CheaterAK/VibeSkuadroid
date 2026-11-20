using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Maps;

/// <summary>Cells (frame labels) and pads of one map file.</summary>
/// <param name="MapFile">The map file as the server names it (<c>moveToArea.strMapFileName</c>).</param>
/// <param name="Cells">Cell names in timeline order.</param>
/// <param name="Pads">Pad names per cell; a cell missing here has no known pads.</param>
/// <param name="Source">"swf", "cache" or why nothing was read ("unsupported: ...", "error: ...").</param>
public sealed record MapCells(string MapFile, IReadOnlyList<string> Cells, IReadOnlyDictionary<string, IReadOnlyList<string>> Pads, string Source)
{
    public bool HasCells => Cells.Count > 0;

    /// <summary>
    /// The pad skua.swf's jumpCorrectRoom would settle on: the requested one when the cell has it, else "Left",
    /// else the cell's first pad; the requested pad when nothing is known about the cell.
    /// </summary>
    public string CorrectPad(string cell, string pad)
    {
        if (!Pads.TryGetValue(cell, out IReadOnlyList<string>? pads) || pads.Count == 0)
            return pad;
        if (pads.Contains(pad))
            return pad;
        return pads.Contains("Left") ? "Left" : pads[0];
    }
}

/// <summary>
/// Map cells from the map SWFs, downloaded once and remembered on disk (one small JSON file per map file), so a
/// headless client knows <c>world.map.currentScene.labels</c> without Flash.
/// </summary>
public sealed class MapCellCache
{
    private readonly Func<string, CancellationToken, Task<byte[]>> _download;
    private readonly string? _directory;
    private readonly ConcurrentDictionary<string, Task<MapCells>> _memory = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="download">Fetches a map file by its server name (for example <c>AqwWebApi.DownloadGameFileAsync("maps/" + file)</c>).</param>
    /// <param name="directory">Where results are kept between runs; null keeps them in memory only.</param>
    public MapCellCache(Func<string, CancellationToken, Task<byte[]>> download, string? directory)
    {
        _download = download;
        _directory = directory;
        if (directory is not null)
            Directory.CreateDirectory(directory);
    }

    /// <summary>Cells of <paramref name="mapFile"/>; never throws (failures come back with no cells and the reason in Source).</summary>
    public Task<MapCells> GetAsync(string mapFile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mapFile))
            return Task.FromResult(new MapCells(mapFile ?? string.Empty, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(), "error: no map file"));
        Task<MapCells> task = _memory.GetOrAdd(mapFile, f => LoadAsync(f, cancellationToken));
        // A failed download is not remembered, so the next map load tries again.
        if (task.IsCompleted && task.Result.Source.StartsWith("error", StringComparison.Ordinal))
            _memory.TryRemove(mapFile, out _);
        return task;
    }

    /// <summary>Puts known cells in the cache (tests, or cells learned elsewhere).</summary>
    public void Remember(MapCells cells)
    {
        _memory[cells.MapFile] = Task.FromResult(cells);
    }

    private async Task<MapCells> LoadAsync(string mapFile, CancellationToken cancellationToken)
    {
        string? path = _directory is null ? null : Path.Combine(_directory, FileNameFor(mapFile));
        if (path is not null && TryReadCache(path, mapFile) is MapCells cached)
            return cached;
        try
        {
            byte[] swf = await _download(mapFile, cancellationToken).ConfigureAwait(false);
            MapCells cells = FromSwf(mapFile, swf);
            if (path is not null && cells.Source == "swf")
                WriteCache(path, cells);
            return cells;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new MapCells(mapFile, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(), "error: " + e.Message);
        }
    }

    /// <summary>Reads cells from SWF bytes (FWS, CWS or ZWS); a file that cannot be read comes back with no cells and an "error: ..." Source.</summary>
    public static MapCells FromSwf(string mapFile, byte[] swf)
    {
        try
        {
            SwfTimeline t = SwfReader.Read(swf);
            List<string> cells = new();
            foreach ((string name, int _) in t.Labels)
            {
                if (!cells.Contains(name))
                    cells.Add(name);
            }
            return new MapCells(mapFile, cells, t.Pads, "swf");
        }
        catch (NotSupportedException)
        {
            return new MapCells(mapFile, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(), "unsupported: lzma");
        }
        catch (SwfFormatException e)
        {
            return new MapCells(mapFile, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(), "error: " + e.Message);
        }
    }

    private static string FileNameFor(string mapFile)
    {
        string safe = new(Path.GetFileNameWithoutExtension(mapFile).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(60).ToArray());
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapFile.ToLowerInvariant())))[..12];
        return $"{safe}-{hash}.cells.json";
    }

    private static MapCells? TryReadCache(string path, string mapFile)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            JsonObject? o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (o?["cells"] is not JsonArray cells)
                return null;
            Dictionary<string, IReadOnlyList<string>> pads = new(StringComparer.Ordinal);
            if (o["pads"] is JsonObject p)
            {
                foreach (KeyValuePair<string, JsonNode?> pair in p)
                    pads[pair.Key] = (pair.Value as JsonArray)?.Select(x => JsonUtil.AsString(x) ?? string.Empty).ToList() ?? new List<string>();
            }
            return new MapCells(mapFile, cells.Select(x => JsonUtil.AsString(x) ?? string.Empty).ToList(), pads, "cache");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string path, MapCells cells)
    {
        try
        {
            JsonObject pads = new();
            foreach (KeyValuePair<string, IReadOnlyList<string>> pair in cells.Pads)
                pads[pair.Key] = new JsonArray(pair.Value.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            JsonObject o = new()
            {
                ["mapFile"] = cells.MapFile,
                ["cells"] = new JsonArray(cells.Cells.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["pads"] = pads,
            };
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs a download next time.
        }
    }
}
