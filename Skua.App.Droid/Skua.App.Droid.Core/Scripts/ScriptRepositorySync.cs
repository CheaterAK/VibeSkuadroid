using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Scripts;

/// <summary>Where a sync is.</summary>
/// <param name="Stage">What it does now ("index", "checking", "downloading", "data", "done").</param>
/// <param name="Done">Files handled in this stage.</param>
/// <param name="Total">Files this stage handles.</param>
/// <param name="Text">A line for the user.</param>
public sealed record RepositorySyncProgress(string Stage, int Done, int Total, string Text)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>What a sync did.</summary>
public sealed record RepositorySyncResult(
    int Listed,
    int Downloaded,
    int UpToDate,
    int Failed,
    int Refused,
    int DataFilesUpdated,
    IReadOnlyList<string> Problems,
    bool RateLimited,
    TimeSpan? RetryAfter,
    bool Cancelled,
    TimeSpan Elapsed)
{
    public bool Succeeded => !RateLimited && !Cancelled && Failed == 0 && Listed > 0;

    public string Summary
    {
        get
        {
            if (Listed == 0)
                return Problems.Count > 0 ? $"Sync failed: {Problems[0]}" : "Sync failed: the script list was empty.";
            string counts = $"{Listed} scripts: {Downloaded} downloaded, {UpToDate} up to date"
                + (Failed > 0 ? $", {Failed} failed" : string.Empty)
                + (Refused > 0 ? $", {Refused} refused" : string.Empty)
                + (DataFilesUpdated > 0 ? $"; {DataFilesUpdated} data file(s) updated" : string.Empty)
                + $" ({Elapsed.TotalSeconds:0.#} s).";
            if (RateLimited)
                return "GitHub is limiting downloads; what was downloaded is kept. "
                    + (RetryAfter is TimeSpan r ? $"Sync again in about {Math.Max(1, (int)Math.Ceiling(r.TotalMinutes))} min. " : "Sync again later. ")
                    + counts;
            if (Cancelled)
                return "Sync cancelled; what was downloaded is kept. " + counts;
            return "Synced. " + counts;
        }
    }
}

/// <summary>Syncs the community script repository into the app's script folder.</summary>
public interface IScriptRepositorySync
{
    bool Available { get; }

    /// <summary>What the sync does and where from, for the Scripts page.</summary>
    string Description { get; }

    Task<RepositorySyncResult> SyncAsync(IProgress<RepositorySyncProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads and updates github.com/auqw/Scripts (branch Skua) into the app-private script folder
/// (<see cref="ScriptLibrary.Root"/>, Skua's ClientFileSources.SkuaScriptsDIR), keeping its folder structure so
/// <c>//cs_include Scripts/...</c> resolves, the way the desktop's GetScriptsService does: the repository's own
/// index (scripts.json: path, size, SHA-256 and raw URL of every script) and one download per new or changed file.
/// Also refreshes the repository's data files Skua reads (AdvancedSkills.json, QuestData.json, JunkItems.json).
/// </summary>
/// <remarks>
/// <para>
/// Only <c>https://raw.githubusercontent.com</c> is contacted: no GitHub API call, so the API's 60 requests per hour
/// for anonymous clients do not apply, and no token is used or needed. Files whose SHA-256 already matches the
/// index are not downloaded again, so a sync that stopped (rate limit, network, cancel) resumes where it was.
/// </para>
/// <para>
/// Not Skua.Core's GetScriptsService: it reaches GitHub through Skua's shared clients (which send an embedded
/// GitHub client credential and cannot be pointed at a test handler) and reports through message boxes.
/// </para>
/// <para>
/// Every path in the index is untrusted and validated exactly like an import
/// (<see cref="ScriptLibrary.ResolveDestination"/>); download URLs must start with <see cref="AllowedDownloadPrefix"/>
/// (the auqw/Scripts repository on raw.githubusercontent.com, any branch), every entry must carry a SHA-256, and a
/// downloaded file is written only when its length and SHA-256 match the entry. The index is capped at
/// <see cref="MaxEntries"/> entries and <see cref="MaxTotalBytes"/> listed bytes; a larger one fails the sync.
/// Files are written beside their target and moved in, so a script starting meanwhile never reads half a file.
/// Local files the repository does not list (the user's own scripts) are never touched.
/// </para>
/// </remarks>
public sealed class ScriptRepositorySync : IScriptRepositorySync, IDisposable
{
    public const string RawHost = "raw.githubusercontent.com";
    public const string BranchRoot = "https://raw.githubusercontent.com/auqw/Scripts/refs/heads/Skua/";
    public const string IndexUrl = BranchRoot + "scripts.json";
    public const long MaxIndexBytes = 32L * 1024 * 1024;

    /// <summary>Every script download URL must start with this (the index uses <c>.../auqw/Scripts/Skua/&lt;path&gt;</c>).</summary>
    public const string AllowedDownloadPrefix = "https://raw.githubusercontent.com/auqw/Scripts/";

    /// <summary>Most entries an index may list (the real one has about 2,000).</summary>
    public const int MaxEntries = 10_000;

    /// <summary>Most bytes the scripts an index lists may add up to (the real ones are about 13 MB).</summary>
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    private readonly ScriptLibrary _library;
    private readonly string _skuaDir;
    private readonly HttpClient _http;
    private readonly int _parallelism;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Action<string>? _warn;
    private int _running;

    /// <param name="library">The script folder (Skua's Scripts folder).</param>
    /// <param name="skuaDir">Skua's data folder (ClientFileSources.SkuaDIR): AdvancedSkills.json, QuestData.json.</param>
    /// <param name="handler">HTTP handler (tests use a fake one); null uses the platform's.</param>
    /// <param name="parallelism">Concurrent downloads.</param>
    /// <param name="retryDelays">Waits before retrying a file after a network or server error.</param>
    /// <param name="warn">Where warnings go (a refused or mismatching download), besides the result's problems.</param>
    public ScriptRepositorySync(ScriptLibrary library, string skuaDir, HttpMessageHandler? handler = null, int parallelism = 6, IReadOnlyList<TimeSpan>? retryDelays = null, Action<string>? warn = null)
    {
        _warn = warn;
        _library = library;
        _skuaDir = skuaDir;
        _parallelism = Math.Clamp(parallelism, 1, 16);
        _retryDelays = retryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VibeSkua-Android", "1.0"));
    }

    public bool Available => true;

    public string Description =>
        "Community scripts: github.com/auqw/Scripts (branch Skua), downloaded from raw.githubusercontent.com into the app's script folder. "
        + "Only new and changed files are downloaded; your own scripts are left alone.";

    /// <summary>A sync is running.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>The repository's data files and where Skua reads them.</summary>
    public IReadOnlyList<(string Url, string Destination)> DataFiles =>
    [
        (BranchRoot + "Skills/AdvancedSkills.json", Path.Combine(_skuaDir, "AdvancedSkills.json")),
        (BranchRoot + "QuestData.json", Path.Combine(_skuaDir, "QuestData.json")),
        (BranchRoot + "JunkItems.json", Path.Combine(_library.Root, "JunkItems.json")),
    ];

    public async Task<RepositorySyncResult> SyncAsync(IProgress<RepositorySyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A sync is already running.");
        try
        {
            return await RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<RepositorySyncResult> RunAsync(IProgress<RepositorySyncProgress>? progress, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        List<string> problems = new();
        progress?.Report(new RepositorySyncProgress("index", 0, 1, "Fetching the script list..."));

        List<IndexEntry> index;
        try
        {
            Fetch fetched = await FetchAsync(IndexUrl, MaxIndexBytes, cancellationToken).ConfigureAwait(false);
            if (fetched.RateLimited)
                return new RepositorySyncResult(0, 0, 0, 0, 0, 0, ["GitHub is limiting requests."], true, fetched.RetryAfter, false, clock.Elapsed);
            if (fetched.Bytes is null)
                return new RepositorySyncResult(0, 0, 0, 0, 0, 0, [$"the script list could not be downloaded ({fetched.Problem})."], false, null, false, clock.Elapsed);
            index = JsonSerializer.Deserialize<List<IndexEntry>>(fetched.Bytes, s_json) ?? new List<IndexEntry>();
            if (index.Count > MaxEntries)
                return Refuse($"the script list has {index.Count} entries, more than the {MaxEntries} allowed; nothing was synced.");
            long listed = 0;
            foreach (IndexEntry entry in index)
                listed += Math.Max(0, entry.Size);
            if (listed > MaxTotalBytes)
                return Refuse($"the script list adds up to {listed / (1024 * 1024)} MB, more than the {MaxTotalBytes / (1024 * 1024)} MB allowed; nothing was synced.");
            TryWrite(Path.Combine(_skuaDir, "scripts_cache.json"), fetched.Bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RepositorySyncResult(0, 0, 0, 0, 0, 0, [], false, null, true, clock.Elapsed);
        }
        catch (JsonException e)
        {
            return new RepositorySyncResult(0, 0, 0, 0, 0, 0, [$"the script list is not valid JSON ({e.Message})."], false, null, false, clock.Elapsed);
        }

        RepositorySyncResult Refuse(string problem)
        {
            Warn("Script sync: " + problem);
            return new RepositorySyncResult(0, 0, 0, 0, 0, 0, [problem], false, null, false, clock.Elapsed);
        }

        // Validate every entry before anything is written.
        List<(IndexEntry Entry, string Destination, Uri Url)> wanted = new();
        int refused = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (IndexEntry entry in index)
        {
            string? destination = _library.ResolveDestination(_library.Root, entry.Path, entry.Size, out string? problem);
            string source = string.IsNullOrWhiteSpace(entry.DownloadUrl) ? BranchRoot + entry.Path : entry.DownloadUrl;
            if (destination is null || !Uri.TryCreate(source, UriKind.Absolute, out Uri? url) || !IsAllowed(url))
            {
                refused++;
                AddProblem(problems, problem ?? $"{entry.Path}: download URL is not under {AllowedDownloadPrefix}");
                continue;
            }
            if (!IsSha256(entry.Sha256) || entry.Size < 0)
            {
                // Without a hash a download cannot be checked; the real index carries one for every script.
                refused++;
                AddProblem(problems, $"{entry.Path}: the script list gives no valid SHA-256 for it");
                continue;
            }
            if (!seen.Add(destination))
                continue; // the same file twice (or twice ignoring case on a case-insensitive file system)
            wanted.Add((entry, destination, url));
        }

        // Check what is already current (SHA-256 against the index).
        List<(IndexEntry Entry, string Destination, Uri Url)> toDownload = new();
        int upToDate = 0;
        for (int i = 0; i < wanted.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new RepositorySyncResult(wanted.Count, 0, upToDate, 0, refused, 0, problems, false, null, true, clock.Elapsed);
            if (i % 100 == 0)
                progress?.Report(new RepositorySyncProgress("checking", i, wanted.Count, $"Checking local files ({i}/{wanted.Count})..."));
            if (IsCurrent(wanted[i].Destination, wanted[i].Entry))
                upToDate++;
            else
                toDownload.Add(wanted[i]);
        }

        int downloaded = 0;
        int failed = 0;
        int done = 0;
        long received = 0;
        bool rateLimited = false;
        TimeSpan? retryAfter = null;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress?.Report(new RepositorySyncProgress("downloading", 0, toDownload.Count, toDownload.Count == 0 ? "All scripts are up to date." : $"Downloading {toDownload.Count} script(s)..."));
        try
        {
            await Parallel.ForEachAsync(toDownload, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = stop.Token }, async (item, ct) =>
            {
                Fetch fetched = await FetchWithRetryAsync(item.Url, Math.Min(ScriptLibrary.MaxFileBytes, item.Entry.Size), ct).ConfigureAwait(false);
                if (fetched.RateLimited)
                {
                    lock (problems)
                    {
                        rateLimited = true;
                        retryAfter = Max(retryAfter, fetched.RetryAfter);
                    }
                    stop.Cancel();
                    return;
                }
                if (fetched.Bytes is not null && Interlocked.Add(ref received, fetched.Bytes.Length) > MaxTotalBytes)
                {
                    // Cannot happen while every file matches its listed size (checked against the cap above); a guard all the same.
                    Interlocked.Increment(ref failed);
                    AddProblem(problems, $"more than {MaxTotalBytes / (1024 * 1024)} MB downloaded; the sync stopped.");
                    stop.Cancel();
                    return;
                }
                if (fetched.Bytes is not null && Mismatch(fetched.Bytes, item.Entry) is string mismatch)
                {
                    Interlocked.Increment(ref failed);
                    AddProblem(problems, $"{item.Entry.Path}: {mismatch}; not written");
                    Warn($"Script sync: {item.Entry.Path} from {item.Url.AbsoluteUri}: {mismatch}; skipped.");
                }
                else if (fetched.Bytes is null || !TryWrite(item.Destination, fetched.Bytes))
                {
                    Interlocked.Increment(ref failed);
                    AddProblem(problems, $"{item.Entry.Path}: {fetched.Problem ?? "could not be written"}");
                }
                else
                {
                    Interlocked.Increment(ref downloaded);
                }
                int now = Interlocked.Increment(ref done);
                progress?.Report(new RepositorySyncProgress("downloading", now, toDownload.Count, $"Downloading ({now}/{toDownload.Count}): {item.Entry.Path}"));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or stopped by a rate limit: what was written stays.
        }

        int dataUpdated = 0;
        if (!rateLimited && !cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new RepositorySyncProgress("data", 0, DataFiles.Count, "Updating skill sets and quest data..."));
            foreach ((string url, string destination) in DataFiles)
            {
                try
                {
                    Fetch fetched = await FetchWithRetryAsync(new Uri(url), MaxIndexBytes, cancellationToken).ConfigureAwait(false);
                    if (fetched.RateLimited)
                    {
                        rateLimited = true;
                        retryAfter = Max(retryAfter, fetched.RetryAfter);
                        break;
                    }
                    if (fetched.Bytes is null || !IsJson(fetched.Bytes))
                    {
                        AddProblem(problems, $"{Path.GetFileName(destination)}: {fetched.Problem ?? "not valid JSON"}");
                        continue;
                    }
                    byte[] bytes = fetched.Bytes;
                    byte[]? local = File.Exists(destination) ? await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false) : null;
                    if (local is not null && destination.EndsWith("QuestData.json", StringComparison.OrdinalIgnoreCase))
                        bytes = MergeQuests(bytes, local);
                    if (local is not null && local.AsSpan().SequenceEqual(bytes))
                        continue;
                    if (TryWrite(destination, bytes))
                        dataUpdated++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        RepositorySyncResult result = new(wanted.Count, downloaded, upToDate, failed, refused, dataUpdated, problems, rateLimited, retryAfter,
            cancellationToken.IsCancellationRequested, clock.Elapsed);
        progress?.Report(new RepositorySyncProgress("done", toDownload.Count, toDownload.Count, result.Summary));
        return result;
    }

    /// <summary>
    /// HTTPS on raw.githubusercontent.com, default port, no query or fragment, under <see cref="AllowedDownloadPrefix"/>,
    /// and no dot segment or backslash in the rest of the path even once unescaped (so <c>%2e%2e</c> cannot climb out).
    /// </summary>
    internal static bool IsAllowed(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals(RawHost, StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) || !string.IsNullOrEmpty(url.UserInfo))
            return false;
        string absolute = url.AbsoluteUri;
        if (!absolute.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal))
            return false;
        string rest = Uri.UnescapeDataString(absolute[AllowedDownloadPrefix.Length..]);
        if (rest.Length == 0 || rest.Contains('\\') || rest.Contains('%'))
            return false;
        foreach (string segment in rest.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        return true;
    }

    private static bool IsSha256(string? hex) =>
        hex is { Length: 64 } && hex.All(Uri.IsHexDigit);

    /// <summary>Why the bytes are not the entry's file, or null when length and SHA-256 match.</summary>
    private static string? Mismatch(byte[] bytes, IndexEntry entry)
    {
        if (bytes.LongLength != entry.Size)
            return $"{bytes.LongLength} bytes, the script list says {entry.Size}";
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            return "SHA-256 does not match the script list";
        return null;
    }

    private void Warn(string message)
    {
        try
        {
            _warn?.Invoke(message);
        }
        catch (Exception)
        {
            // A failing log must not fail the sync.
        }
    }

    private static bool IsCurrent(string path, IndexEntry entry)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length != entry.Size || string.IsNullOrEmpty(entry.Sha256))
                return false;
            using FileStream stream = file.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<Fetch> FetchWithRetryAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            Fetch fetched = await FetchAsync(url.AbsoluteUri, maxBytes, cancellationToken).ConfigureAwait(false);
            if (fetched.Bytes is not null || fetched.RateLimited || !fetched.Retryable || attempt >= _retryDelays.Count)
                return fetched;
            await Task.Delay(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Fetch> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRateLimit(response))
                return Fetch.Limited(RetryAfterOf(response));
            if (!response.IsSuccessStatusCode)
                return Fetch.Failed($"HTTP {(int)response.StatusCode}", (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout);
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                return Fetch.Failed("too large", false);
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return Fetch.Failed("too large", false);
                buffer.Write(chunk, 0, read);
            }
            return Fetch.Ok(buffer.ToArray());
        }
        catch (HttpRequestException e)
        {
            return Fetch.Failed(e.Message, true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fetch.Failed("timed out", true);
        }
        catch (IOException e)
        {
            return Fetch.Failed(e.Message, true);
        }
    }

    // GitHub answers a limited client with 429, or 403 carrying the rate limit headers.
    private static bool IsRateLimit(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        return response.Headers.RetryAfter is not null
            || (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining) && remaining.FirstOrDefault() == "0");
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            return date - DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? reset) && long.TryParse(reset.FirstOrDefault(), out long epoch))
            return DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
        return null;
    }

    // Skua adds quests it sees in game to its QuestData.json; like the desktop updater, keep those the repository lacks.
    private static byte[] MergeQuests(byte[] remote, byte[] local)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(remote) is not System.Text.Json.Nodes.JsonArray remoteQuests
                || System.Text.Json.Nodes.JsonNode.Parse(local) is not System.Text.Json.Nodes.JsonArray localQuests)
                return remote;
            HashSet<int> ids = new(remoteQuests.Select(QuestId).OfType<int>());
            List<System.Text.Json.Nodes.JsonNode> extra = localQuests.Where(q => q is not null && QuestId(q) is int id && !ids.Contains(id)).Select(q => q!.DeepClone()).ToList();
            if (extra.Count == 0)
                return remote;
            foreach (System.Text.Json.Nodes.JsonNode q in extra)
                remoteQuests.Add(q);
            return JsonSerializer.SerializeToUtf8Bytes(remoteQuests, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return remote;
        }
    }

    private static int? QuestId(System.Text.Json.Nodes.JsonNode? quest) =>
        quest is System.Text.Json.Nodes.JsonObject o && (o["ID"] ?? o["Id"] ?? o["id"]) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out int id) ? id : null;

    private static TimeSpan? Max(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static bool IsJson(byte[] bytes)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddProblem(List<string> problems, string problem)
    {
        lock (problems)
        {
            if (problems.Count < 50)
                problems.Add(problem);
        }
    }

    // Written beside the target and moved in (a script starting meanwhile never reads half a file).
    private static bool TryWrite(string destination, byte[] bytes)
    {
        string temp = $"{destination}.{Guid.NewGuid():N}.sync-tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, destination, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record Fetch(byte[]? Bytes, string? Problem, bool Retryable, bool RateLimited, TimeSpan? RetryAfter)
    {
        public static Fetch Ok(byte[] bytes) => new(bytes, null, false, false, null);

        public static Fetch Failed(string problem, bool retryable) => new(null, problem, retryable, false, null);

        public static Fetch Limited(TimeSpan? retryAfter) => new(null, "rate limited", false, true, retryAfter);
    }

    /// <summary>One entry of scripts.json (Skua.Core's ScriptInfo, the fields the sync needs).</summary>
    private sealed class IndexEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }
    }
}
