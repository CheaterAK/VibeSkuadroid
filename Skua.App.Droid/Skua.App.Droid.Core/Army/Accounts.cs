using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Core.Army;

/// <summary>How an army account runs.</summary>
public enum AccountMode
{
    /// <summary>Not part of the army (kept in the list only).</summary>
    Off,

    /// <summary>
    /// A light session in the main process: Skua.Core.Headless only (socket, world model, client rules), native
    /// behaviours (follow, fight, pick up drops, quest loop). No Skua.Core, no Roslyn, no scripts. Tens of MB, not hundreds.
    /// </summary>
    Light,

    /// <summary>A full engine (Skua.Core, real scripts) in a slot of its own: slot 1 is the main process, 2..6 are ":acctN".</summary>
    Full,
}

/// <summary>
/// An army account as the account manager keeps it (<c>files/droid/accounts.json</c>). No secret: the password is in the
/// Keystore vault under <see cref="AccountVault.EntryFor"/>.
/// </summary>
/// <param name="Id">Stable id (12 hex characters); names the vault entry and is what the coordinator sends a slot.</param>
/// <param name="Username">The AQW account name.</param>
/// <param name="DisplayName">Optional label (the desktop Manager's display name).</param>
/// <param name="Server">Preferred server; null picks the least full usable one.</param>
/// <param name="Tags">Groups (the desktop Manager's tags), e.g. "farm", "army A".</param>
/// <param name="Mode">Light, full or off.</param>
/// <param name="FullSlot">For <see cref="AccountMode.Full"/>: the slot (1 = main process, 2..6 = own process).</param>
public sealed record ArmyAccount(
    string Id,
    string Username,
    string? DisplayName = null,
    string? Server = null,
    IReadOnlyList<string>? Tags = null,
    AccountMode Mode = AccountMode.Light,
    int? FullSlot = null)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName!;

    public IReadOnlyList<string> TagList => Tags ?? [];
}

/// <summary>The account list and the army's slot settings.</summary>
public sealed record ArmyState
{
    public IReadOnlyList<ArmyAccount> Accounts { get; init; } = [];

    /// <summary>Full slots the army may use (1..<see cref="SlotIds.MaxFullSlots"/>), default <see cref="SlotIds.DefaultFullSlots"/>.</summary>
    public int FullSlots { get; init; } = SlotIds.DefaultFullSlots;

    /// <summary>The Butler roster: the master's account id (null: none).</summary>
    public string? MasterId { get; init; }

    /// <summary>Accounts that follow the master (Butler followers).</summary>
    public IReadOnlyList<string>? FollowerIds { get; init; }

    /// <summary>Recent Jump All destinations ("map|cell|pad"), newest first.</summary>
    public IReadOnlyList<string>? RecentJumps { get; init; }
}

/// <summary>Slot numbers.</summary>
public static class SlotIds
{
    /// <summary>Slot 1 is the main process; 2..<see cref="MaxFullSlots"/> are the processes ":acct2".. declared in the manifest.</summary>
    public const int MaxFullSlots = 6;

    public const int DefaultFullSlots = 2;

    public const int MainSlot = 1;

    public static bool IsRemote(int slot) => slot is > MainSlot and <= MaxFullSlots;

    /// <summary>The Android process name suffix of a remote slot.</summary>
    public static string ProcessSuffix(int slot) => IsRemote(slot) ? $":acct{slot}" : throw new ArgumentOutOfRangeException(nameof(slot));

    /// <summary>The slot of a process name ("com.vibeskua.droid:acct3" is 3), or null for any other process.</summary>
    public static int? FromProcessName(string? processName)
    {
        if (processName is null)
            return null;
        int colon = processName.LastIndexOf(':');
        if (colon < 0)
            return null;
        string suffix = processName[(colon + 1)..];
        return suffix.StartsWith("acct", StringComparison.Ordinal) && int.TryParse(suffix.AsSpan(4), out int n) && IsRemote(n) && suffix == $"acct{n}" ? n : null;
    }
}

/// <summary>
/// Reads and writes <see cref="ArmyState"/> (<c>files/droid/accounts.json</c>). The main process writes it (atomic
/// replace); slot processes only read it to find the account a login names.
/// </summary>
public sealed partial class AccountStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private ArmyState _current;

    public AccountStore(string path)
    {
        _path = path;
        _current = Read(path);
    }

    public ArmyState Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event EventHandler<ArmyState>? Changed;

    /// <summary>Re-reads the file (slot processes, before a login).</summary>
    public ArmyState Reload()
    {
        ArmyState state = Read(_path);
        lock (_gate)
            _current = state;
        return state;
    }

    public ArmyAccount? Find(string? id) => id is null ? null : Current.Accounts.FirstOrDefault(a => a.Id == id);

    public ArmyState Update(Func<ArmyState, ArmyState> change)
    {
        ArmyState next;
        lock (_gate)
        {
            next = Normalize(change(_current));
            Write(_path, next);
            _current = next;
        }
        Changed?.Invoke(this, next);
        return next;
    }

    /// <summary>Adds or replaces an account (validated); returns it.</summary>
    public ArmyAccount Save(ArmyAccount account)
    {
        Validate(account);
        Update(s =>
        {
            if (s.Accounts.Any(a => a.Id != account.Id && string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{account.Username} is already in the list.");
            List<ArmyAccount> list = s.Accounts.Where(a => a.Id != account.Id).ToList();
            int index = s.Accounts.ToList().FindIndex(a => a.Id == account.Id);
            list.Insert(index < 0 ? list.Count : index, account);
            return s with { Accounts = list };
        });
        return account;
    }

    public void Remove(string id) => Update(s => s with
    {
        Accounts = s.Accounts.Where(a => a.Id != id).ToList(),
        MasterId = s.MasterId == id ? null : s.MasterId,
        FollowerIds = s.FollowerIds?.Where(f => f != id).ToList(),
    });

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static bool IsValidUsername(string? name) => name is not null && UserPattern().IsMatch(name.Trim());

    public static void Validate(ArmyAccount a)
    {
        if (!IsValidId(a.Id))
            throw new ArgumentException("Invalid account id.");
        if (!IsValidUsername(a.Username))
            throw new ArgumentException("Enter the account name (letters, digits, spaces, - and _).");
        if (a.DisplayName is { Length: > 40 })
            throw new ArgumentException("The display name is too long.");
        if (a.Server is { Length: > 40 })
            throw new ArgumentException("The server name is too long.");
        if (a.TagList.Count > 10 || a.TagList.Any(t => t.Length is 0 or > 24))
            throw new ArgumentException("At most 10 tags of 1-24 characters.");
        if (a.Mode == AccountMode.Full && a.FullSlot is not int slot)
            throw new ArgumentException("A full account needs a slot.");
        if (a.FullSlot is int s && (s < SlotIds.MainSlot || s > SlotIds.MaxFullSlots))
            throw new ArgumentException($"Slots are 1 to {SlotIds.MaxFullSlots}.");
    }

    /// <summary>One account per full slot (the last assignment wins), slots within <see cref="ArmyState.FullSlots"/>, a valid roster.</summary>
    public static ArmyState Normalize(ArmyState s)
    {
        int fullSlots = Math.Clamp(s.FullSlots, 1, SlotIds.MaxFullSlots);
        HashSet<int> taken = new();
        List<ArmyAccount> accounts = new();
        foreach (ArmyAccount a in s.Accounts.Reverse())
        {
            ArmyAccount fixedUp = a;
            if (a.Mode == AccountMode.Full && (a.FullSlot is not int slot || slot > fullSlots || !taken.Add(slot)))
                fixedUp = a with { Mode = AccountMode.Light, FullSlot = null };
            else if (a.Mode != AccountMode.Full)
                fixedUp = a with { FullSlot = null };
            accounts.Insert(0, fixedUp);
        }
        HashSet<string> ids = accounts.Select(a => a.Id).ToHashSet();
        return s with
        {
            FullSlots = fullSlots,
            Accounts = accounts,
            MasterId = s.MasterId is string m && ids.Contains(m) ? m : null,
            FollowerIds = s.FollowerIds?.Where(ids.Contains).Where(f => f != s.MasterId).Distinct().ToList(),
            RecentJumps = s.RecentJumps?.Take(8).ToList(),
        };
    }

    private static ArmyState Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ArmyState();
            return Normalize(JsonSerializer.Deserialize<ArmyState>(File.ReadAllText(path), s_json) ?? new ArmyState());
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ArmyState();
        }
    }

    private static void Write(string path, ArmyState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, s_json));
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[0-9a-f]{12}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\- ]{1,32}\z")]
    private static partial Regex UserPattern();
}

/// <summary>
/// The army accounts' passwords: one Keystore-encrypted entry per account (<c>aqw-acct-&lt;id&gt;</c>), next to the
/// single-account entry (<see cref="CredentialVault.EntryName"/>, untouched). Each entry binds the password to the
/// account name, like the single-account vault. Every process opens the same store: the main process saves and
/// deletes, and each slot process decrypts only its own entry by behaviour (the account it was asked to log in). This
/// is not a security boundary: slot processes run as the same Linux user as the main process (one app UID, one
/// Keystore), so code running in any of them, a script included, could read every entry.
/// </summary>
public sealed class AccountVault
{
    public const string EntryPrefix = "aqw-acct-";

    private readonly ISecretStore _store;

    public AccountVault(ISecretStore store)
    {
        _store = store;
    }

    public bool IsAvailable => _store.IsAvailable;

    public static string EntryFor(string accountId) =>
        AccountStore.IsValidId(accountId) ? EntryPrefix + accountId : throw new ArgumentException("Invalid account id.", nameof(accountId));

    public bool HasPassword(string accountId) => _store.IsAvailable && _store.Contains(EntryFor(accountId));

    public void Save(ArmyAccount account, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!_store.IsAvailable)
            throw new InvalidOperationException("No secure storage: the password cannot be remembered on this device.");
        byte[] blob = CredentialVault.Encode(account.Username.Trim(), password);
        try
        {
            _store.Save(EntryFor(account.Id), blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>The password saved for <paramref name="account"/> if it was saved for that account name, else null with why.</summary>
    public string? Load(ArmyAccount account, out VaultStatus status)
    {
        status = VaultStatus.None;
        if (!_store.IsAvailable)
            return null;
        string entry = EntryFor(account.Id);
        SecretLookup lookup = _store.Load(entry);
        if (lookup.Status == SecretStatus.Unavailable)
        {
            status = VaultStatus.Unavailable;
            return null;
        }
        if (lookup.Value is not byte[] blob)
            return null;
        try
        {
            if (!CredentialVault.TryDecode(blob, out string? user, out string? password))
            {
                _store.Delete(entry);
                return null;
            }
            if (!string.Equals(user, account.Username.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            status = VaultStatus.Found;
            return password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void Forget(string accountId) => _store.Delete(EntryFor(accountId));
}
