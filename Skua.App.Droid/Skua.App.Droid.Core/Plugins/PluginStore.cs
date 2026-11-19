using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Plugins;

/// <summary>One built-in plugin's saved state: on or off, and its options as text (like Skua's option files).</summary>
public sealed record PluginState
{
    public bool Enabled { get; init; } = true;

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The built-in plugins' state, as JSON in app-private storage (<c>files/droid/plugins.json</c>, next to the app's
/// settings). Saved atomically (temp file, then rename); a damaged file starts from defaults. No secrets.
/// </summary>
public sealed class PluginStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginState> _plugins;

    public PluginStore(string path)
    {
        _path = path;
        _plugins = Load();
    }

    public string FilePath => _path;

    /// <summary>Raised after a change was saved, with the plugin's id.</summary>
    public event EventHandler<string>? Changed;

    public PluginState Get(string id)
    {
        lock (_gate)
            return _plugins.TryGetValue(id, out PluginState? s) ? Copy(s) : new PluginState();
    }

    public bool IsEnabled(string id) => Get(id).Enabled;

    public void SetEnabled(string id, bool enabled) => Update(id, s => s with { Enabled = enabled });

    public string? GetOption(string id, string key) => Get(id).Options.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Replaces the given options (others are kept).</summary>
    public void SetOptions(string id, IReadOnlyDictionary<string, string> options) => Update(id, s =>
    {
        Dictionary<string, string> merged = new(s.Options, StringComparer.Ordinal);
        foreach ((string key, string value) in options)
            merged[key] = value;
        return s with { Options = merged };
    });

    public void SetOption(string id, string key, string value) => SetOptions(id, new Dictionary<string, string> { [key] = value });

    private void Update(string id, Func<PluginState, PluginState> change)
    {
        lock (_gate)
        {
            PluginState current = _plugins.TryGetValue(id, out PluginState? s) ? s : new PluginState();
            PluginState updated = change(Copy(current));
            if (updated.Enabled == current.Enabled && updated.Options.Count == current.Options.Count &&
                updated.Options.All(p => current.Options.TryGetValue(p.Key, out string? v) && v == p.Value))
                return;
            _plugins[id] = updated;
            Save();
        }
        Changed?.Invoke(this, id);
    }

    private static PluginState Copy(PluginState s) => s with { Options = new Dictionary<string, string>(s.Options, StringComparer.Ordinal) };

    private Dictionary<string, PluginState> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Dictionary<string, PluginState>? loaded = JsonSerializer.Deserialize<Dictionary<string, PluginState>>(File.ReadAllText(_path), s_json);
                if (loaded is not null)
                    return loaded.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => Copy(p.Value), StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file must not stop the app; start from defaults.
        }
        return new Dictionary<string, PluginState>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_plugins, s_json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run; the next change tries again.
        }
    }
}
