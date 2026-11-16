using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Core.Headless.Net;

/// <summary>Where the client version used for the socket login came from.</summary>
public enum ClientVersionSource
{
    /// <summary>Set explicitly by the caller.</summary>
    Configured,

    /// <summary>Read from the live game SWF named by <c>api/data/gameversion</c> (Game.as <c>vParam</c>).</summary>
    GameFile,

    /// <summary>The game server rejected the login and named the version it requires.</summary>
    ServerRequired,

    /// <summary>Nothing could be discovered; the built-in fallback was used.</summary>
    Fallback,
}

/// <summary>A resolved client version and how it was found.</summary>
public sealed record ClientVersionResult(string Version, ClientVersionSource Source, string Detail);

/// <summary>
/// The client version sent in the login nick (<c>SPIDER#0001~user~&lt;version&gt;</c>). The server silently loads no
/// character for a stale one, so it is discovered at run time: first from the live game SWF, then from the
/// server's own rejection message, with <see cref="Fallback"/> as the last resort.
/// </summary>
public static partial class ClientVersion
{
    /// <summary>
    /// Last known good value. aqx (2026-11-13) defaults to 5.32; Seraph's fallback is 5.01 (Game4000r1). The newer
    /// one is used. It is only a fallback: see <see cref="ClientVersionResolver"/>.
    /// </summary>
    public const string Fallback = "5.32";

    public static bool IsValid(string? version)
    {
        return version is not null && VersionRegex().IsMatch(version);
    }

    /// <summary>
    /// Reads Game.as <c>vParam</c> from a game SWF. In the SWF's ABC string pool the constant name is followed by
    /// its value's length byte and the value ("vParam", len, "5.32"). FWS (plain) and CWS (zlib) are read; ZWS
    /// (LZMA) is not supported and returns null. Approach from Seraph's updater (MIT, Ezzeldeen2006).
    /// </summary>
    public static string? FromSwf(ReadOnlySpan<byte> swf)
    {
        if (swf.Length < 8)
            return null;
        byte[] body;
        if (swf[0] == (byte)'F' && swf[1] == (byte)'W' && swf[2] == (byte)'S')
        {
            body = swf[8..].ToArray();
        }
        else if (swf[0] == (byte)'C' && swf[1] == (byte)'W' && swf[2] == (byte)'S')
        {
            try
            {
                using MemoryStream input = new(swf[8..].ToArray());
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                using MemoryStream output = new();
                zlib.CopyTo(output);
                body = output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        Match match = VParamRegex().Match(Encoding.Latin1.GetString(body));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The version named in a loginResponse refusal such as "Server requires AQW game version 5.40 or higher."
    /// (format from aqx, MIT, toommyliu).
    /// </summary>
    public static string? RequiredFromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;
        Match match = RequiredRegex().Match(message);
        return match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("vParam[\\s\\S]([0-9]+\\.[0-9]+)")]
    private static partial Regex VParamRegex();

    [GeneratedRegex("requires AQW game version ([0-9][0-9.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredRegex();
}

/// <summary>Finds the live client version over HTTP, caching the answer per game file.</summary>
public sealed class ClientVersionResolver
{
    private readonly AqwWebApi _api;
    private string? _cachedFile;
    private string? _cachedVersion;

    public ClientVersionResolver(AqwWebApi api)
    {
        _api = api;
    }

    /// <summary>
    /// <c>api/data/gameversion</c> -> download <c>gamefiles/&lt;sFile&gt;</c> -> read <c>vParam</c>. Any failure
    /// falls back to <see cref="ClientVersion.Fallback"/> and says why in <see cref="ClientVersionResult.Detail"/>.
    /// </summary>
    public async Task<ClientVersionResult> ResolveAsync(CancellationToken cancellationToken = default)
    {
        GameVersionInfo info;
        try
        {
            info = await _api.GetGameVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AqwLoginException e)
        {
            return new(ClientVersion.Fallback, ClientVersionSource.Fallback, $"game version lookup failed: {e.Message}");
        }

        if (string.IsNullOrWhiteSpace(info.GameFile))
            return new(ClientVersion.Fallback, ClientVersionSource.Fallback, "game version reply has no sFile");

        if (info.GameFile == _cachedFile && _cachedVersion is not null)
            return new(_cachedVersion, ClientVersionSource.GameFile, $"cached from {info.GameFile}");

        byte[] swf;
        try
        {
            swf = await _api.DownloadGameFileAsync(info.GameFile, cancellationToken).ConfigureAwait(false);
        }
        catch (AqwLoginException e)
        {
            return new(ClientVersion.Fallback, ClientVersionSource.Fallback, $"could not download {info.GameFile}: {e.Message}");
        }

        string? version = ClientVersion.FromSwf(swf);
        if (!ClientVersion.IsValid(version))
        {
            string kind = swf.Length >= 3 ? Encoding.ASCII.GetString(swf, 0, 3) : "?";
            return new(ClientVersion.Fallback, ClientVersionSource.Fallback, $"no vParam found in {info.GameFile} (signature {kind})");
        }

        _cachedFile = info.GameFile;
        _cachedVersion = version;
        return new(version!, ClientVersionSource.GameFile, $"read from {info.GameFile}");
    }
}
