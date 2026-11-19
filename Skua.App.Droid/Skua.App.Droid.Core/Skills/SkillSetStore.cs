using Skua.App.Droid.Core.Engine;
using Skua.Core.Models.Skills;
using Skua.Core.Skills;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.App.Droid.Core.Skills;

/// <summary>Where a rotation comes from, relative to the community file (<c>AdvancedSkills.json</c>).</summary>
public enum SkillSetOrigin
{
    /// <summary>The same as in the community file.</summary>
    Community,

    /// <summary>The community file has this class and mode, and the user changed it.</summary>
    Edited,

    /// <summary>Only in the user's file (created by the user, or no longer in the community file).</summary>
    Yours,
}

/// <summary>
/// One rotation ("skill set") of a class in one mode, as Skua keeps it in memory (<see cref="AdvancedSkill"/>): the
/// skill string (<c>1 H&gt;70% | 2 | 3S</c>), use mode, timeout and the combo reset flag.
/// </summary>
public sealed record SkillSet(
    string ClassName,
    string Mode,
    string Skills,
    SkillUseMode UseMode,
    int Timeout,
    bool ResetComboOnTargetChange,
    SkillSetOrigin Origin = SkillSetOrigin.Yours)
{
    /// <summary>Desktop's one-line form (<see cref="AdvancedSkill.ToString"/> without the class).</summary>
    public string UseModeText => UseMode == SkillUseMode.UseIfAvailable ? "Use if available" : $"Wait for cooldown ({Timeout} ms)";
}

/// <summary>
/// Skua's skill sets on disk: <c>AdvancedSkills.json</c> (the community file, refreshed by the repository sync) and
/// <c>UserAdvancedSkills.json</c> (what Skua's <c>AdvancedSkillContainer</c> loads and scripts use), both in Skua's
/// data folder. Reads and writes the same JSON as the desktop (<see cref="AdvancedSkillsConfigJson"/>; a saved
/// rotation goes through Skua's own <see cref="AdvancedSkillsParser.ParseSkillString"/>, as the desktop editor's save
/// does); rotations that are not edited are kept as they are in the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Community updates and the user's edits.</b> <c>UserAdvancedSkills.base.json</c> next to the user file records
/// which community file the user file was last brought up to date with. When the community file changes,
/// <see cref="UpdateFromCommunity"/> merges per class and mode: a rotation the user did not change takes the new
/// community version, an edited one stays as the user left it, one the user deleted stays deleted, a new community one
/// is added, and the user's own rotations are kept. The desktop (AdvancedSkillContainer.RefreshDefaultSkillsAsync)
/// only adds new classes and modes, so on the desktop rotations the user never touched stop following the community
/// file once the user file exists, and deleted ones come back; edits are kept on both.
/// </para>
/// <para>
/// Only <see cref="ClassUseMode"/> names are written as modes: Skua's loader drops every skill set when one mode does
/// not parse.
/// </para>
/// </remarks>
public sealed class SkillSetStore
{
    public const string DefaultsFileName = "AdvancedSkills.json";
    public const string UserFileName = "UserAdvancedSkills.json";
    public const string BaseFileName = "UserAdvancedSkills.base.json";

    private static readonly object s_gate = new();

    // The container's own SaveToJson options.
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SkillSetStore(string skuaDir)
    {
        SkuaDir = skuaDir;
    }

    public string SkuaDir { get; }

    /// <summary>The community skill sets (refreshed by the repository sync).</summary>
    public string DefaultsFile => Path.Combine(SkuaDir, DefaultsFileName);

    /// <summary>The skill sets Skua loads and scripts use.</summary>
    public string UserFile => Path.Combine(SkuaDir, UserFileName);

    /// <summary>The community file the user file was last brought up to date with.</summary>
    public string BaseFile => Path.Combine(SkuaDir, BaseFileName);

    /// <summary>The mode names Skua accepts, in its order.</summary>
    public static IReadOnlyList<string> Modes { get; } = Enum.GetNames<ClassUseMode>();

    /// <summary>
    /// Every rotation Skua would load (the user file; the community file when there is no user file yet), sorted by
    /// class (ignoring case) and mode (Skua's mode order).
    /// </summary>
    /// <exception cref="EngineException">A file cannot be read.</exception>
    public IReadOnlyList<SkillSet> Load()
    {
        lock (s_gate)
        {
            JsonObject defaults = ReadObject(DefaultsFile) ?? new JsonObject();
            JsonObject user = ReadObject(UserFile) ?? defaults;
            List<SkillSet> sets = new();
            foreach ((string className, JsonNode? modes) in user)
            {
                if (modes is not JsonObject modeObject)
                    continue;
                foreach ((string mode, JsonNode? node) in modeObject)
                {
                    if (node is null || !Enum.TryParse(mode, ignoreCase: true, out ClassUseMode _))
                        continue;
                    SkillModeJson? json = Deserialize(node);
                    if (json is null)
                        continue;
                    JsonNode? community = (defaults[className] as JsonObject)?[mode];
                    SkillSetOrigin origin = community is null ? SkillSetOrigin.Yours
                        : JsonNode.DeepEquals(community, node) ? SkillSetOrigin.Community
                        : SkillSetOrigin.Edited;
                    sets.Add(FromJson(className, mode, json, origin));
                }
            }
            return sets
                .OrderBy(s => s.ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => Enum.TryParse(s.Mode, true, out ClassUseMode m) ? (int)m : int.MaxValue)
                .ToList();
        }
    }

    /// <summary>The community version of a rotation, or null.</summary>
    public SkillSet? Community(string className, string mode)
    {
        lock (s_gate)
        {
            JsonNode? node = (ReadObject(DefaultsFile)?[className] as JsonObject)?[mode];
            return node is not null && Deserialize(node) is SkillModeJson json ? FromJson(className, mode, json, SkillSetOrigin.Community) : null;
        }
    }

    /// <summary>
    /// Saves a rotation to the user file (created from the community file if missing). With
    /// <paramref name="replacesClass"/>/<paramref name="replacesMode"/> (a rotation renamed or moved to another mode),
    /// that one is removed.
    /// </summary>
    /// <exception cref="EngineException">Not a valid rotation, or the user file cannot be read or written.</exception>
    public void Save(SkillSet set, string? replacesClass = null, string? replacesMode = null)
    {
        string className = set.ClassName.Trim();
        if (className.Length == 0)
            throw new EngineException("Enter the class name (as the game shows it, e.g. Void Highlord).");
        if (!Enum.TryParse(set.Mode, ignoreCase: false, out ClassUseMode _) || !Modes.Contains(set.Mode))
            throw new EngineException($"\"{set.Mode}\" is not a mode Skua knows ({string.Join(", ", Modes)}).");
        List<AdvancedSkillJson> skills = AdvancedSkillsParser.ParseSkillString(set.Skills);
        if (skills.Count == 0)
            throw new EngineException("Add at least one skill.");

        SkillModeJson json = new()
        {
            SkillUseMode = set.UseMode == SkillUseMode.UseIfAvailable ? "UseIfAvailable" : "WaitForCooldown",
            SkillTimeout = set.Timeout,
            ResetComboOnTargetChange = set.ResetComboOnTargetChange,
            Skills = skills,
        };
        lock (s_gate)
        {
            JsonObject user = EnsureUserAndBase();
            if (replacesClass is not null && replacesMode is not null)
                RemoveEntry(user, replacesClass, replacesMode);
            if (user[className] is not JsonObject modes)
                user[className] = modes = new JsonObject();
            modes[set.Mode] = JsonSerializer.SerializeToNode(json, s_write);
            Write(UserFile, user);
        }
    }

    /// <summary>Removes a rotation from the user file.</summary>
    public void Delete(string className, string mode)
    {
        lock (s_gate)
        {
            JsonObject user = EnsureUserAndBase();
            if (RemoveEntry(user, className, mode))
                Write(UserFile, user);
        }
    }

    /// <summary>Puts the community version of a rotation back; false when the community file has none.</summary>
    public bool RestoreCommunity(string className, string mode)
    {
        lock (s_gate)
        {
            JsonNode? community = (ReadObject(DefaultsFile)?[className] as JsonObject)?[mode];
            if (community is null)
                return false;
            JsonObject user = EnsureUserAndBase();
            if (user[className] is not JsonObject modes)
                user[className] = modes = new JsonObject();
            modes[mode] = community.DeepClone();
            Write(UserFile, user);
            return true;
        }
    }

    /// <summary>Replaces the user file with the community file (the desktop's "Reset Sets").</summary>
    /// <exception cref="EngineException">There is no community file (sync the community scripts).</exception>
    public void ResetAll()
    {
        lock (s_gate)
        {
            JsonObject defaults = ReadObject(DefaultsFile) ?? throw new EngineException("No community skill sets yet: sync the community scripts first.");
            Write(UserFile, defaults);
            Write(BaseFile, defaults);
        }
    }

    /// <summary>
    /// Brings the user file up to date with a changed community file, keeping the user's edits (see the class
    /// remarks). True when the user file changed (Skua's container should reload).
    /// </summary>
    /// <exception cref="EngineException">A file cannot be read or written.</exception>
    public bool UpdateFromCommunity()
    {
        lock (s_gate)
        {
            if (!File.Exists(DefaultsFile))
                return false;
            string defaultsText = File.ReadAllText(DefaultsFile);
            JsonObject defaults = Parse(defaultsText, DefaultsFile);
            if (!File.Exists(UserFile))
            {
                Write(UserFile, defaults);
                Write(BaseFile, defaults);
                return true;
            }
            JsonObject user = EnsureUserAndBase();
            JsonObject? previous = ReadObject(BaseFile);
            if (previous is not null && JsonNode.DeepEquals(previous, defaults))
                return false;
            previous ??= new JsonObject();

            bool changed = false;
            foreach ((string className, JsonNode? communityModes) in defaults)
            {
                if (communityModes is not JsonObject cModes)
                    continue;
                JsonObject? uModes = user[className] as JsonObject;
                JsonObject? bModes = previous[className] as JsonObject;
                foreach ((string mode, JsonNode? community) in cModes)
                {
                    if (community is null)
                        continue;
                    JsonNode? mine = uModes?[mode];
                    JsonNode? was = bModes?[mode];
                    if (mine is null)
                    {
                        if (was is not null)
                            continue; // deleted by the user
                        if (uModes is null)
                            user[className] = uModes = new JsonObject();
                        uModes[mode] = community.DeepClone();
                        changed = true;
                    }
                    else if (was is not null && JsonNode.DeepEquals(mine, was) && !JsonNode.DeepEquals(mine, community))
                    {
                        uModes![mode] = community.DeepClone(); // untouched: follow the community file
                        changed = true;
                    }
                }
            }
            if (changed)
                Write(UserFile, user);
            Write(BaseFile, defaults);
            return changed;
        }
    }

    // The user file, created from the community file if missing; and the base it follows: for a user file from before
    // the base existed, the user file itself (the app had no editor, so it was always a copy of the community file).
    private JsonObject EnsureUserAndBase()
    {
        JsonObject? user = ReadObject(UserFile);
        if (user is null)
        {
            JsonObject defaults = ReadObject(DefaultsFile) ?? new JsonObject();
            Write(UserFile, defaults);
            Write(BaseFile, defaults);
            return (JsonObject)defaults.DeepClone();
        }
        if (!File.Exists(BaseFile))
            Write(BaseFile, user);
        return user;
    }

    private static bool RemoveEntry(JsonObject user, string className, string mode)
    {
        if (user[className] is not JsonObject modes || !modes.Remove(mode))
            return false;
        if (modes.Count == 0)
            user.Remove(className);
        return true;
    }

    private static SkillSet FromJson(string className, string mode, SkillModeJson json, SkillSetOrigin origin) => new(
        className,
        mode,
        AdvancedSkillsParser.ToSkillString(json.Skills),
        json.SkillUseMode == "UseIfAvailable" ? SkillUseMode.UseIfAvailable : SkillUseMode.WaitForCooldown, // as the container reads it
        json.SkillTimeout,
        json.ResetComboOnTargetChange,
        origin);

    private static SkillModeJson? Deserialize(JsonNode node)
    {
        try
        {
            return node.Deserialize<SkillModeJson>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? ReadObject(string file)
    {
        try
        {
            return File.Exists(file) ? Parse(File.ReadAllText(file), file) : null;
        }
        catch (IOException e)
        {
            throw new EngineException($"Could not read {Path.GetFileName(file)}: {e.Message}");
        }
    }

    private static JsonObject Parse(string text, string file)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new EngineException($"{Path.GetFileName(file)} does not hold skill sets.");
        }
        catch (JsonException e)
        {
            throw new EngineException($"{Path.GetFileName(file)} is not valid JSON ({e.Message}). Reset the skill sets to the community version to start over.");
        }
    }

    private static void Write(string file, JsonObject content)
    {
        string? dir = Path.GetDirectoryName(file);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        string temp = file + ".tmp";
        try
        {
            File.WriteAllText(temp, content.ToJsonString(s_write));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EngineException($"Could not write {Path.GetFileName(file)}: {e.Message}");
        }
    }
}
