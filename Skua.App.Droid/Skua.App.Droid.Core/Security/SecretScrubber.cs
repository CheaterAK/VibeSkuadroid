namespace Skua.App.Droid.Core.Security;

/// <summary>
/// Defence in depth for everything the engine emits (log lines, captured packets): replaces any registered secret
/// (the account password, the socket login token) with a fixed marker. The protocol layer already redacts the login
/// token from outgoing packets; this also covers a server echoing it or an exception message quoting it.
/// </summary>
public sealed class SecretScrubber
{
    public const string Marker = "<redacted>";

    // Shorter values would match ordinary text and make logs unreadable without protecting anything real.
    private const int MinimumLength = 4;

    private volatile string[] _secrets = [];
    private readonly object _lock = new();

    /// <summary>Registers a secret. Values shorter than 4 characters are ignored.</summary>
    public void Add(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLength)
            return;
        lock (_lock)
        {
            if (Array.IndexOf(_secrets, secret) >= 0)
                return;
            // Longest first, so a secret containing another is replaced whole.
            _secrets = _secrets.Append(secret).OrderByDescending(s => s.Length).ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
            _secrets = [];
    }

    public string Scrub(string text)
    {
        string[] secrets = _secrets;
        if (secrets.Length == 0 || string.IsNullOrEmpty(text))
            return text;
        foreach (string secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, Marker, StringComparison.Ordinal);
        }
        return text;
    }
}
