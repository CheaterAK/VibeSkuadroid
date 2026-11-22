namespace Skua.App.Droid.Core.Security;

/// <summary>
/// Defence in depth for everything the engine emits (log lines, captured packets): replaces any registered secret
/// (the account password, the socket login token) with a fixed marker. The protocol layer already redacts the login
/// token from outgoing packets; this also covers a server echoing it or an exception message quoting it.
/// </summary>
/// <remarks>
/// A scrubber can include other scrubbers' secrets (<see cref="Link"/>): the process-wide diagnostics registry links
/// each light army account's own scrubber, so crash and exit reports, canary samples and the bundle scrub that
/// account's password and token too, while the account's logout (<see cref="Clear"/> of its own scrubber) and
/// disposal (<see cref="Unlink"/>) touch no other account's secrets.
/// </remarks>
public sealed class SecretScrubber
{
    public const string Marker = "<redacted>";

    // Shorter values would match ordinary text and make logs unreadable without protecting anything real.
    private const int MinimumLength = 4;

    private volatile string[] _secrets = [];
    private volatile SecretScrubber[] _linked = [];
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

    /// <summary>Forgets this scrubber's own secrets (linked scrubbers keep theirs and stay linked).</summary>
    public void Clear()
    {
        lock (_lock)
            _secrets = [];
    }

    /// <summary>From now on <see cref="Scrub"/> also removes <paramref name="other"/>'s secrets (as they are at each call).</summary>
    public void Link(SecretScrubber other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this))
            return;
        lock (_lock)
        {
            if (Array.IndexOf(_linked, other) < 0)
                _linked = [.. _linked, other];
        }
    }

    public void Unlink(SecretScrubber other)
    {
        lock (_lock)
            _linked = _linked.Where(s => !ReferenceEquals(s, other)).ToArray();
    }

    /// <summary>The secrets <see cref="Scrub"/> removes: this scrubber's and its linked scrubbers' own (one level).</summary>
    private string[] Secrets()
    {
        string[] own = _secrets;
        SecretScrubber[] linked = _linked;
        if (linked.Length == 0)
            return own;
        return own.Concat(linked.SelectMany(s => s._secrets)).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    public string Scrub(string text)
    {
        string[] secrets = Secrets();
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
