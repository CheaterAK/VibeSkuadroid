using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Droid.Core.Security;

/// <summary>
/// Platform storage for small secrets. The Android implementation (KeystoreSecretStore) encrypts with an
/// AES-256-GCM key that never leaves the Android Keystore. Implementations must never log the plaintext.
/// </summary>
public interface ISecretStore
{
    /// <summary>False where no hardware-backed or OS-protected store exists; the app then offers no "remember me".</summary>
    bool IsAvailable { get; }

    /// <summary>Encrypts and stores <paramref name="plaintext"/> under <paramref name="name"/>, replacing any previous value.</summary>
    void Save(string name, ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// The decrypted value, or null when there is none or it cannot be decrypted (key deleted, data restored from
    /// another device, tampering). An undecryptable entry is deleted.
    /// </summary>
    byte[]? Load(string name);

    void Delete(string name);
}

/// <summary>A store that stores nothing (desktop preview host, or a device where the Keystore failed).</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public void Save(string name, ReadOnlySpan<byte> plaintext) => throw new InvalidOperationException("No secure storage on this platform.");

    public byte[]? Load(string name) => null;

    public void Delete(string name)
    {
    }
}

/// <summary>
/// The single remembered account password. The stored value binds the password to the account name, so a password
/// is only ever offered for the account it was saved with.
/// </summary>
public sealed class CredentialVault
{
    /// <summary>Entry name in the secret store.</summary>
    public const string EntryName = "aqw-account";

    private const byte FormatVersion = 1;

    private readonly ISecretStore _store;

    public CredentialVault(ISecretStore store)
    {
        _store = store;
    }

    public bool IsAvailable => _store.IsAvailable;

    /// <summary>Stores the password for <paramref name="username"/>, replacing any other account's.</summary>
    public void Save(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!_store.IsAvailable)
            return;
        byte[] blob = Encode(username.Trim(), password);
        try
        {
            _store.Save(EntryName, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>The stored password if it was saved for <paramref name="username"/> (case-insensitive), else null.</summary>
    public string? TryLoad(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || !_store.IsAvailable)
            return null;
        byte[]? blob = _store.Load(EntryName);
        if (blob is null)
            return null;
        try
        {
            if (!TryDecode(blob, out string? storedUser, out string? password))
            {
                _store.Delete(EntryName);
                return null;
            }
            return string.Equals(storedUser, username.Trim(), StringComparison.OrdinalIgnoreCase) ? password : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>The account a password is stored for, if any.</summary>
    public string? StoredAccount()
    {
        if (!_store.IsAvailable)
            return null;
        byte[]? blob = _store.Load(EntryName);
        if (blob is null)
            return null;
        try
        {
            return TryDecode(blob, out string? user, out _) ? user : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void Forget() => _store.Delete(EntryName);

    // Layout: [version:1][userLength:2 BE][user UTF-8][password UTF-8]
    internal static byte[] Encode(string username, string password)
    {
        int userBytes = Encoding.UTF8.GetByteCount(username);
        int passBytes = Encoding.UTF8.GetByteCount(password);
        if (userBytes > ushort.MaxValue)
            throw new ArgumentException("Account name too long.", nameof(username));
        byte[] blob = new byte[1 + 2 + userBytes + passBytes];
        blob[0] = FormatVersion;
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(1, 2), (ushort)userBytes);
        Encoding.UTF8.GetBytes(username, blob.AsSpan(3, userBytes));
        Encoding.UTF8.GetBytes(password, blob.AsSpan(3 + userBytes));
        return blob;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> blob, out string? username, out string? password)
    {
        username = null;
        password = null;
        if (blob.Length < 4 || blob[0] != FormatVersion)
            return false;
        int userBytes = BinaryPrimitives.ReadUInt16BigEndian(blob.Slice(1, 2));
        if (3 + userBytes >= blob.Length)
            return false;
        try
        {
            UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            username = strict.GetString(blob.Slice(3, userBytes));
            password = strict.GetString(blob[(3 + userBytes)..]);
        }
        catch (DecoderFallbackException)
        {
            username = null;
            password = null;
            return false;
        }
        return username.Length > 0 && password.Length > 0;
    }
}
