using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Droid.Core.Security;

/// <summary>Outcome of reading a secret.</summary>
public enum SecretStatus
{
    /// <summary>The entry was read and decrypted.</summary>
    Found,

    /// <summary>
    /// There is no entry, or there was one that can never be decrypted again (wrong key, tampered or malformed data,
    /// permanently invalidated key) and it has been deleted.
    /// </summary>
    NotFound,

    /// <summary>
    /// The entry exists but could not be read right now (the Keystore was busy, locked or failed transiently). It is
    /// kept: a later attempt may succeed.
    /// </summary>
    Unavailable,
}

/// <summary>Result of <see cref="ISecretStore.Load"/>. <see cref="Value"/> is set only for <see cref="SecretStatus.Found"/>.</summary>
public readonly record struct SecretLookup(SecretStatus Status, byte[]? Value)
{
    public static SecretLookup NotFound => new(SecretStatus.NotFound, null);

    public static SecretLookup Unavailable => new(SecretStatus.Unavailable, null);

    public static SecretLookup Found(byte[] value) => new(SecretStatus.Found, value);
}

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
    /// Reads and decrypts an entry. Only a definitive failure (see <see cref="SecretStatus.NotFound"/>) deletes the
    /// entry; a transient one returns <see cref="SecretStatus.Unavailable"/> and keeps it.
    /// </summary>
    SecretLookup Load(string name);

    /// <summary>An entry exists (nothing is decrypted).</summary>
    bool Contains(string name);

    void Delete(string name);
}

/// <summary>A store that stores nothing (desktop preview host, or a device where the Keystore failed).</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public void Save(string name, ReadOnlySpan<byte> plaintext) => throw new InvalidOperationException("No secure storage on this platform.");

    public SecretLookup Load(string name) => SecretLookup.NotFound;

    public bool Contains(string name) => false;

    public void Delete(string name)
    {
    }
}

/// <summary>
/// Which secret-store failures are definitive (the entry can never be decrypted again, so it is deleted and the user
/// is asked for the password) and which are transient (the entry is kept and the caller falls back for now).
/// </summary>
/// <remarks>
/// Platform neutral so it can be tested off the device: the Android store passes the Java class names of the
/// exception and its causes. Anything not listed is treated as transient: deleting a still-valid password because the
/// Keystore hiccuped would break unattended reconnects, while keeping an unusable blob costs nothing.
/// </remarks>
public static class SecretFailurePolicy
{
    /// <summary>Simple class names that mean "this entry is gone for good".</summary>
    public static IReadOnlyCollection<string> DefinitiveFailures { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // GCM tag mismatch: different key (reinstall, key regenerated), tampered or truncated data.
        "AEADBadTagException",
        // The key was invalidated by the OS (e.g. secure lock screen removed for keys bound to it).
        "KeyPermanentlyInvalidatedException",
        // Our own: the file does not have the expected layout.
        "InvalidDataException",
        // The key alias no longer exists, so nothing stored under it can be decrypted.
        "MissingKeyException",
    };

    /// <summary>True when any exception in the chain (outermost first) is a definitive failure.</summary>
    public static bool IsDefinitive(IEnumerable<string> exceptionChainClassNames) =>
        exceptionChainClassNames.Any(name => DefinitiveFailures.Contains(SimpleName(name)));

    private static string SimpleName(string name)
    {
        int dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('$'));
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}

/// <summary>What the vault found for an account.</summary>
public enum VaultStatus
{
    /// <summary>A password is stored for this account.</summary>
    Found,

    /// <summary>No password is stored for this account (none at all, another account's, or it was unusable and deleted).</summary>
    None,

    /// <summary>A password is stored but cannot be read right now; ask the user or try again later.</summary>
    Unavailable,
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

    /// <summary>An encrypted entry exists. Cheap: nothing is decrypted (use it for UI state, not as proof it can be read).</summary>
    public bool HasStoredPassword => _store.IsAvailable && _store.Contains(EntryName);

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
    public string? TryLoad(string? username) => Load(username, out _);

    /// <summary>
    /// The stored password if it was saved for <paramref name="username"/> (case-insensitive), else null, with why.
    /// Decrypts: call it when the password is needed, not to update UI state.
    /// </summary>
    public string? Load(string? username, out VaultStatus status)
    {
        status = VaultStatus.None;
        if (string.IsNullOrWhiteSpace(username) || !_store.IsAvailable)
            return null;
        SecretLookup lookup = _store.Load(EntryName);
        if (lookup.Status == SecretStatus.Unavailable)
        {
            status = VaultStatus.Unavailable;
            return null;
        }
        if (lookup.Value is not byte[] blob)
            return null;
        try
        {
            if (!TryDecode(blob, out string? storedUser, out string? password))
            {
                // Decrypted fine but not our layout: definitively unusable.
                _store.Delete(EntryName);
                return null;
            }
            if (!string.Equals(storedUser, username.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            status = VaultStatus.Found;
            return password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>The account a password is stored for, if any. Decrypts; prefer the settings metadata for UI state.</summary>
    public string? StoredAccount()
    {
        if (!_store.IsAvailable)
            return null;
        if (_store.Load(EntryName).Value is not byte[] blob)
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
