using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Skua.App.Droid.Core.Security;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Droid.Security;

/// <summary>
/// <see cref="ISecretStore"/> backed by the Android Keystore.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One AES-256 key, alias <see cref="KeyAlias"/>, generated inside the Android Keystore (hardware-backed TEE
/// on current phones). It is non-exportable: the app can only ask the Keystore to encrypt/decrypt with it.</item>
/// <item>AES-GCM, 128-bit tag, a fresh random IV from the Keystore per encryption (randomized encryption is
/// enforced by the key), the entry name as associated data, so a blob cannot be swapped between entries.</item>
/// <item>No user authentication is required for the key: the engine must log back in unattended (reconnect,
/// restart). Anyone who can run code as this app can therefore decrypt; the protection is against reading the
/// file (backups are disabled, the file is in no_backup/, and the key cannot leave the device).</item>
/// <item>The file holds [version][iv length][iv][ciphertext+tag]; no plaintext, no key material.</item>
/// <item>Any failure to decrypt (key invalidated, data from another install, tampering) deletes the entry: the
/// user is simply asked for the password again.</item>
/// <item>Nothing here logs plaintext, ciphertext or exception messages that could contain either.</item>
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
    private readonly object _lock = new();

    public KeystoreSecretStore(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            _ = GetOrCreateKey();
            IsAvailable = true;
        }
        catch (Exception e) when (e is Java.Lang.Exception or IOException or UnauthorizedAccessException)
        {
            // No usable Keystore: the app then offers no "remember password".
            Android.Util.Log.Warn("VibeSkua", $"Keystore unavailable ({e.GetType().Name}); passwords will not be remembered.");
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
                Cipher cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
                byte[] iv = cipher.GetIV() ?? throw new InvalidOperationException("The Keystore returned no IV.");
                cipher.UpdateAAD(Encoding.UTF8.GetBytes(name));
                byte[] sealedData = cipher.DoFinal(clear) ?? throw new InvalidOperationException("Encryption returned nothing.");

                byte[] file = new byte[2 + iv.Length + sealedData.Length];
                file[0] = FileVersion;
                file[1] = checked((byte)iv.Length);
                iv.CopyTo(file, 2);
                sealedData.CopyTo(file, 2 + iv.Length);
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

    public byte[]? Load(string name)
    {
        if (!IsAvailable)
            return null;
        string path = PathFor(name);
        lock (_lock)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                byte[] file = File.ReadAllBytes(path);
                if (file.Length < 2 + 12 + 16 || file[0] != FileVersion)
                    throw new InvalidDataException("unknown format");
                int ivLength = file[1];
                if (ivLength < 12 || 2 + ivLength >= file.Length)
                    throw new InvalidDataException("bad IV length");
                byte[] iv = file.AsSpan(2, ivLength).ToArray();
                byte[] sealedData = file.AsSpan(2 + ivLength).ToArray();

                Cipher cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));
                cipher.UpdateAAD(Encoding.UTF8.GetBytes(name));
                return cipher.DoFinal(sealedData);
            }
            catch (Exception e) when (e is Java.Lang.Exception or IOException or InvalidDataException or InvalidOperationException)
            {
                Android.Util.Log.Warn("VibeSkua", $"Stored secret '{name}' could not be decrypted ({e.GetType().Name}); it was deleted.");
                TryDelete(path);
                return null;
            }
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
            TryDelete(PathFor(name));
    }

    private static IKey GetOrCreateKey()
    {
        KeyStore keyStore = KeyStore.GetInstance(KeystoreProvider)!;
        keyStore.Load(null);
        if (keyStore.GetKey(KeyAlias, null) is IKey existing)
            return existing;

        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetRandomizedEncryptionRequired(true)
            .Build());
        return generator.GenerateKey()!;
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
}
