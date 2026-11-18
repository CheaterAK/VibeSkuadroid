using Android.Content;
using Android.Content.PM;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Droid.Security;

/// <summary>
/// <see cref="ISecretStore"/> backed by the Android Keystore.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One AES-256 key, alias <see cref="KeyAlias"/>, generated inside the Android Keystore and non-exportable: the
/// app can only ask the Keystore to encrypt/decrypt with it. A new key is put in StrongBox (the phone's secure element)
/// when the device has one and a round trip through it works; otherwise in the TEE as before. An existing key is never
/// replaced, so entries written by earlier versions keep working with no migration.</item>
/// <item>AES-GCM, 128-bit tag, a fresh random IV from the Keystore per encryption (randomized encryption is
/// enforced by the key), the entry name as associated data, so a blob cannot be swapped between entries.</item>
/// <item>No user authentication and no "unlocked device required" for the key: the engine must log back in
/// unattended after a memory restart, which typically happens with the screen locked. Anyone who can run code as this
/// app can therefore decrypt; the protection is against reading the file (backups are disabled, the file is in
/// no_backup/, and the key cannot leave the device).</item>
/// <item>The file holds [version][iv length][iv][ciphertext+tag]; no plaintext, no key material.</item>
/// <item>Only a definitive decryption failure deletes the entry (<see cref="SecretFailurePolicy"/>: GCM tag mismatch,
/// malformed file, permanently invalidated or missing key). Anything else (Keystore busy or failing transiently)
/// returns <see cref="SecretStatus.Unavailable"/> and keeps the entry, so a resume falls back to the "tap to reconnect"
/// notice instead of silently losing the password.</item>
/// <item>The key is only created on the first save, never on the startup path.</item>
/// <item>Nothing here logs plaintext, ciphertext or exception messages that could contain either (types only).</item>
/// </list>
/// </remarks>
internal sealed class KeystoreSecretStore : ISecretStore
{
    public const string KeyAlias = "com.vibeskua.droid.credentials.v1";

    private const string KeystoreProvider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;
    private const byte FileVersion = 1;

    private readonly string _directory;
    private readonly Context _context;
    private readonly object _lock = new();

    public KeystoreSecretStore(Context context, string directory)
    {
        _context = context;
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            // Only checks that the Keystore can be opened; the key itself is created on the first save.
            _ = OpenKeyStore();
            IsAvailable = true;
        }
        catch (Exception e) when (e is Java.Lang.Exception or IOException or UnauthorizedAccessException)
        {
            // No usable Keystore: the app then offers no "remember password".
            CrashCapture.Info($"Keystore unavailable ({e.GetType().Name}); passwords will not be remembered.");
            IsAvailable = false;
        }
    }

    public bool IsAvailable { get; }

    public void Save(string name, ReadOnlySpan<byte> plaintext)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The Android Keystore is not available.");
        string path = PathFor(name);
        byte[] clear = plaintext.ToArray();
        try
        {
            lock (_lock)
            {
                byte[] file = Encrypt(GetOrCreateKey(), name, clear);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, file);
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public SecretLookup Load(string name)
    {
        if (!IsAvailable)
            return SecretLookup.NotFound;
        string path = PathFor(name);
        lock (_lock)
        {
            byte[] file;
            try
            {
                if (!File.Exists(path))
                    return SecretLookup.NotFound;
                file = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CrashCapture.Info($"Stored secret '{name}' could not be read ({e.GetType().Name}); kept for a later attempt.");
                return SecretLookup.Unavailable;
            }

            try
            {
                IKey key = GetExistingKey() ?? throw new MissingKeyException();
                return SecretLookup.Found(Decrypt(key, name, file));
            }
            catch (Exception e) when (e is Java.Lang.Exception or InvalidDataException or MissingKeyException or InvalidOperationException)
            {
                if (SecretFailurePolicy.IsDefinitive(ExceptionChain(e)))
                {
                    CrashCapture.Info($"Stored secret '{name}' can no longer be decrypted ({e.GetType().Name}); it was deleted.");
                    TryDelete(path);
                    return SecretLookup.NotFound;
                }
                CrashCapture.Info($"Stored secret '{name}' could not be decrypted right now ({e.GetType().Name}); kept for a later attempt.");
                return SecretLookup.Unavailable;
            }
        }
    }

    public bool Contains(string name)
    {
        if (!IsAvailable)
            return false;
        try
        {
            return File.Exists(PathFor(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
            TryDelete(PathFor(name));
    }

    private static byte[] Encrypt(IKey key, string name, byte[] clear)
    {
        Cipher cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
        byte[] iv = cipher.GetIV() ?? throw new InvalidOperationException("The Keystore returned no IV.");
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(name));
        byte[] sealedData = cipher.DoFinal(clear) ?? throw new InvalidOperationException("Encryption returned nothing.");

        byte[] file = new byte[2 + iv.Length + sealedData.Length];
        file[0] = FileVersion;
        file[1] = checked((byte)iv.Length);
        iv.CopyTo(file, 2);
        sealedData.CopyTo(file, 2 + iv.Length);
        return file;
    }

    private static byte[] Decrypt(IKey key, string name, byte[] file)
    {
        if (file.Length < 2 + 12 + 16 || file[0] != FileVersion)
            throw new InvalidDataException("unknown format");
        int ivLength = file[1];
        if (ivLength < 12 || 2 + ivLength >= file.Length)
            throw new InvalidDataException("bad IV length");
        byte[] iv = file.AsSpan(2, ivLength).ToArray();
        byte[] sealedData = file.AsSpan(2 + ivLength).ToArray();

        Cipher cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, iv));
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(name));
        return cipher.DoFinal(sealedData) ?? throw new InvalidOperationException("Decryption returned nothing.");
    }

    private static KeyStore OpenKeyStore()
    {
        KeyStore keyStore = KeyStore.GetInstance(KeystoreProvider)!;
        keyStore.Load(null);
        return keyStore;
    }

    private static IKey? GetExistingKey() => OpenKeyStore().GetKey(KeyAlias, null) as IKey;

    private IKey GetOrCreateKey()
    {
        if (GetExistingKey() is IKey existing)
            return existing;

        if (HasStrongBox())
        {
            try
            {
                IKey strongBox = GenerateKey(strongBox: true);
                // Some secure elements accept the key but fail AES-GCM with it later; prove it works now.
                byte[] probe = Decrypt(strongBox, "probe", Encrypt(strongBox, "probe", [1, 2, 3, 4]));
                if (probe.Length == 4 && probe[3] == 4)
                {
                    CrashCapture.Info("Credential key created in StrongBox.");
                    return strongBox;
                }
            }
            catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException or InvalidDataException)
            {
                CrashCapture.Info($"StrongBox key not usable ({e.GetType().Name}); using the TEE.");
            }
            TryDeleteKey();
        }

        IKey key = GenerateKey(strongBox: false);
        CrashCapture.Info("Credential key created in the TEE.");
        return key;
    }

    private static IKey GenerateKey(bool strongBox)
    {
        KeyGenParameterSpec.Builder spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetRandomizedEncryptionRequired(true);
        if (strongBox && OperatingSystem.IsAndroidVersionAtLeast(28))
            spec.SetIsStrongBoxBacked(true);
        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)!;
        generator.Init(spec.Build());
        return generator.GenerateKey()!;
    }

    private bool HasStrongBox()
    {
        try
        {
            return OperatingSystem.IsAndroidVersionAtLeast(28)
                && _context.PackageManager?.HasSystemFeature(PackageManager.FeatureStrongboxKeystore) == true;
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    private static void TryDeleteKey()
    {
        try
        {
            OpenKeyStore().DeleteEntry(KeyAlias);
        }
        catch (Java.Lang.Exception)
        {
        }
    }

    /// <summary>Class names of the exception and its causes (Java causes for Java exceptions).</summary>
    private static IEnumerable<string> ExceptionChain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            yield return x.GetType().Name;
        if (e is Java.Lang.Throwable throwable)
        {
            Java.Lang.Throwable? cause = throwable;
            for (int i = 0; cause is not null && i < 8; i++, cause = cause.Cause)
                yield return cause.Class?.Name ?? string.Empty;
        }
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Invalid secret name.", nameof(name));
        return Path.Combine(_directory, name + ".bin");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The key alias is gone: nothing stored under it can be decrypted again.</summary>
    private sealed class MissingKeyException : Exception
    {
        public MissingKeyException()
            : base("The credential key does not exist.")
        {
        }
    }
}
