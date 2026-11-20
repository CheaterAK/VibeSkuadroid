using System.Security.Cryptography;
using System.Text;

namespace RuffleSpike.Core;

/// <summary>
/// The random path secret of the socket bridge. It is created per game view session, handed to the page only in
/// the app-served config.json, and required as the first path segment of every WebSocket request, so another app
/// on the phone that finds the loopback port still cannot use the bridge.
/// </summary>
public sealed class BridgeSecret
{
    private readonly byte[] _ascii;

    public BridgeSecret(string value)
    {
        if (value.Length < 32 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("secret must be at least 32 url-safe characters", nameof(value));
        Value = value;
        _ascii = Encoding.ASCII.GetBytes(value);
    }

    public string Value { get; }

    /// <summary>A new secret of 32 random bytes, base64url without padding (43 characters).</summary>
    public static BridgeSecret Create()
    {
        string s = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new BridgeSecret(s);
    }

    /// <summary>Constant-time comparison (for equal lengths).</summary>
    public bool Matches(string candidate)
    {
        if (candidate.Length != _ascii.Length || candidate.Any(c => c > 127)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), _ascii);
    }

    public override string ToString() => "(secret)";
}
