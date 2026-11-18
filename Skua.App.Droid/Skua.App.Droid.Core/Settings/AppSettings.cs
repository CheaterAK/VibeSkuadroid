using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Settings;

/// <summary>
/// User settings. Stored as JSON in app-private storage. Never holds a secret: the password lives only in the
/// Keystore-backed <see cref="Security.CredentialVault"/>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Last account name used (shown pre-filled on the login screen).</summary>
    public string? AccountName { get; init; }

    /// <summary>Preferred server; null picks the least full usable one.</summary>
    public string? Server { get; init; }

    /// <summary>Keep the password (encrypted with a Keystore key) to log in again after a restart.</summary>
    public bool RememberPassword { get; init; }

    /// <summary>
    /// The account the remembered password is bound to (metadata only, so the UI can tell whether a password is
    /// remembered without decrypting it). Null in settings written before it existed: then <see cref="AccountName"/>,
    /// which was always saved together with the password, is used.
    /// </summary>
    public string? RememberedAccount { get; init; }

    /// <summary>Forces the AQW client version (blank discovers it from the live game file).</summary>
    public string? ClientVersion { get; init; }

    public bool AutoReconnect { get; init; } = true;

    /// <summary>RSS (MB) above which the engine process should be restarted.</summary>
    public int MemoryThresholdMb { get; init; } = 1024;

    /// <summary>Restart the engine process automatically when over the threshold and no script runs.</summary>
    public bool AutoRestartOnMemory { get; init; } = true;

    /// <summary>Keep Wi-Fi in its high performance mode while a script runs.</summary>
    public bool WifiLockWhileScriptRuns { get; init; } = true;

    /// <summary>When the last automatic memory restart happened (loop protection across processes).</summary>
    public DateTimeOffset? LastAutoRestartAt { get; init; }

    /// <summary>The user dismissed the battery optimisation prompt.</summary>
    public bool BatteryPromptDismissed { get; init; }

    public const int MinMemoryThresholdMb = 384;
    public const int MaxMemoryThresholdMb = 8192;

    /// <summary>Clamps values a hand-edited or older file could carry.</summary>
    public AppSettings Normalized() => this with
    {
        MemoryThresholdMb = Math.Clamp(MemoryThresholdMb, MinMemoryThresholdMb, MaxMemoryThresholdMb),
        AccountName = string.IsNullOrWhiteSpace(AccountName) ? null : AccountName.Trim(),
        RememberedAccount = string.IsNullOrWhiteSpace(RememberedAccount) ? null : RememberedAccount.Trim(),
        Server = string.IsNullOrWhiteSpace(Server) ? null : Server.Trim(),
        ClientVersion = string.IsNullOrWhiteSpace(ClientVersion) ? null : ClientVersion.Trim(),
    };
}

/// <summary>Loads and saves <see cref="AppSettings"/> atomically (write to a temp file, then rename).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = LoadFromDisk();
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public string FilePath => _path;

    /// <summary>Applies <paramref name="change"/> to the current settings and saves the result.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_lock)
        {
            updated = change(_current).Normalized();
            if (updated == _current)
                return updated;
            Save(updated);
            _current = updated;
        }
        Changed?.Invoke(this, updated);
        return updated;
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (File.Exists(_path))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), s_json) ?? new AppSettings()).Normalized();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the app; start from defaults.
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, s_json));
        File.Move(temp, _path, overwrite: true);
    }
}
