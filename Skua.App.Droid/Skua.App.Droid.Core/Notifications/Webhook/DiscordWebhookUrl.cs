using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>
/// A validated Discord webhook URL: <c>https://{discord.com|discordapp.com, or their canary./ptb. subdomains}/api[/v{n}]/webhooks/{id}/{token}</c>,
/// optionally with <c>?thread_id={id}</c> (a forum or thread). Anything else is refused: another host or scheme,
/// a port, credentials, a fragment, other query parameters, dot segments or escapes in the path.
/// </summary>
/// <remarks>The URL is a credential (anyone holding it can post as the webhook): <see cref="ToString"/> never shows it.</remarks>
public sealed partial class DiscordWebhookUrl
{
    /// <summary>The only hosts a webhook may be on.</summary>
    public static IReadOnlyCollection<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "discord.com",
        "discordapp.com",
        "canary.discord.com",
        "ptb.discord.com",
        "canary.discordapp.com",
        "ptb.discordapp.com",
    };

    private DiscordWebhookUrl(Uri uri, string id, string token)
    {
        Uri = uri;
        WebhookId = id;
        Token = token;
    }

    /// <summary>The URL to POST to.</summary>
    public Uri Uri { get; }

    /// <summary>The webhook's id (not secret on its own).</summary>
    public string WebhookId { get; }

    /// <summary>The webhook's token (secret).</summary>
    public string Token { get; }

    /// <summary>"Discord webhook …1234": enough to recognise it, nothing usable.</summary>
    public string Display => $"Discord webhook on {Uri.Host}, id ending {WebhookId[^Math.Min(4, WebhookId.Length)..]}";

    public override string ToString() => Display;

    /// <summary>Parses and validates <paramref name="text"/>; on failure <paramref name="error"/> says why (without echoing the input).</summary>
    public static bool TryParse(string? text, out DiscordWebhookUrl? url, out string error)
    {
        url = null;
        string input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            error = "Enter the webhook URL.";
            return false;
        }
        if (input.Length > 512 || input.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c > '~'))
        {
            error = "That is not a webhook URL.";
            return false;
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
        {
            error = "That is not a URL.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps || !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "The webhook URL must start with https://.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || input.Contains('@'))
        {
            error = "The webhook URL must not contain a user name or password.";
            return false;
        }
        string host = uri.IdnHost.ToLowerInvariant();
        if (!AllowedHosts.Contains(host) || uri.HostNameType != UriHostNameType.Dns)
        {
            error = "Only Discord webhooks are allowed (discord.com or discordapp.com).";
            return false;
        }
        if (!uri.IsDefaultPort)
        {
            error = "The webhook URL must not name a port.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Fragment) || input.Contains('#'))
        {
            error = "The webhook URL must not contain '#'.";
            return false;
        }
        // The path as typed (Uri would have resolved dot segments and unescaped some characters).
        int pathStart = input.IndexOf('/', "https://".Length);
        string rawPath = pathStart < 0 ? string.Empty : input[pathStart..];
        int query = rawPath.IndexOf('?');
        string rawQuery = query < 0 ? string.Empty : rawPath[(query + 1)..];
        if (query >= 0)
            rawPath = rawPath[..query];
        Match m = PathPattern().Match(rawPath);
        if (!m.Success)
        {
            error = "That is not a Discord webhook URL (expected …/api/webhooks/<id>/<token>).";
            return false;
        }
        if (rawQuery.Length > 0 && !QueryPattern().IsMatch(rawQuery))
        {
            error = "The webhook URL may only carry a thread_id.";
            return false;
        }
        string id = m.Groups["id"].Value;
        string token = m.Groups["token"].Value;
        Uri canonical = new($"https://{host}/api/webhooks/{id}/{token}{(rawQuery.Length > 0 ? "?" + rawQuery : string.Empty)}");
        url = new DiscordWebhookUrl(canonical, id, token);
        error = string.Empty;
        return true;
    }

    [GeneratedRegex(@"^/api(/v\d{1,2})?/webhooks/(?<id>\d{15,25})/(?<token>[A-Za-z0-9_\-]{20,200})/?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^thread_id=\d{15,25}\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueryPattern();
}

/// <summary>
/// The Discord webhook URL, encrypted in the app's secret store (the Android Keystore-backed vault that holds the
/// remembered password, under its own entry). Never in the settings file, never logged, excluded from backups
/// (the secret store lives in <c>no_backup/</c> and the app allows no backup at all).
/// </summary>
public sealed class WebhookUrlVault
{
    /// <summary>Entry name in the secret store.</summary>
    public const string EntryName = "discord-webhook";

    private const byte FormatVersion = 1;

    private readonly ISecretStore _store;

    public WebhookUrlVault(ISecretStore store)
    {
        _store = store;
    }

    /// <summary>False where no secure storage exists: the webhook cannot be configured then.</summary>
    public bool IsAvailable => _store.IsAvailable;

    /// <summary>An encrypted entry exists (nothing is decrypted).</summary>
    public bool HasUrl => _store.IsAvailable && _store.Contains(EntryName);

    public void Save(DiscordWebhookUrl url)
    {
        if (!_store.IsAvailable)
            throw new InvalidOperationException("No secure storage on this device: the webhook URL cannot be saved.");
        byte[] text = Encoding.UTF8.GetBytes(url.Uri.AbsoluteUri);
        byte[] blob = new byte[1 + 2 + text.Length];
        try
        {
            blob[0] = FormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(1, 2), (ushort)text.Length);
            text.CopyTo(blob.AsSpan(3));
            _store.Save(EntryName, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>The stored URL, or null (none, unreadable right now, or no longer valid: then it is deleted).</summary>
    public DiscordWebhookUrl? Load(out SecretStatus status)
    {
        status = SecretStatus.NotFound;
        if (!_store.IsAvailable)
            return null;
        SecretLookup lookup = _store.Load(EntryName);
        status = lookup.Status;
        if (lookup.Value is not byte[] blob)
            return null;
        try
        {
            if (blob.Length < 3 || blob[0] != FormatVersion)
            {
                _store.Delete(EntryName);
                status = SecretStatus.NotFound;
                return null;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(1, 2));
            if (3 + length != blob.Length || !DiscordWebhookUrl.TryParse(Encoding.UTF8.GetString(blob, 3, length), out DiscordWebhookUrl? url, out _))
            {
                _store.Delete(EntryName);
                status = SecretStatus.NotFound;
                return null;
            }
            return url;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void Forget() => _store.Delete(EntryName);
}
