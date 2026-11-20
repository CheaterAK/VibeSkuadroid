using System.Text.Json;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Core.Army;

/// <summary>What a slot remembers about its session across its own restarts (<c>droid/slot.json</c>; no secrets).</summary>
public sealed record SlotSessionState(string? AccountId, string? Server, bool Online);

/// <summary>
/// The platform-neutral side of a full slot process (2..6): its <see cref="EngineHost"/> (engine, settings, memory
/// watchdog, script queue), the account it logs in, by id, with the password it reads from the vault itself, and its
/// own resume after a restart of its process. The Android slot service exposes it through a <see cref="SlotEndpoint"/>.
/// </summary>
public sealed class SlotHost : ISlotHost
{
    private readonly AccountStore _accounts;
    private readonly AccountVault _vault;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private SlotSessionState _state;
    private volatile bool _restarting;

    public SlotHost(int slot, EngineHost host, SlotLayout layout, AccountStore accounts, AccountVault vault)
    {
        Slot = slot;
        Host = host;
        Layout = layout;
        _accounts = accounts;
        _vault = vault;
        _stateFile = Path.Combine(layout.Paths.AppDataDir, "slot.json");
        _state = ReadState(_stateFile);
        // The watchdog's restart of this slot only drops a session it can restore: the account's password is in the vault.
        host.CanResumeOverride = () => host.Engine.Snapshot.Connection is ConnectionPhase.Offline or ConnectionPhase.Failed
            || (CurrentAccount() is ArmyAccount a && _vault.HasPassword(a.Id));
        // A restart of this process logs out first; the slot must still log back in afterwards.
        host.RestartStarting += (_, _) => _restarting = true;
        host.Engine.StopRequested += (_, request) =>
        {
            if (request == EngineStopRequest.Logout && !_restarting)
                SetState(s => s with { Online = false });
        };
    }

    public int Slot { get; }

    public EngineHost Host { get; }

    public SlotLayout Layout { get; }

    public IBotEngine Engine => Host.Engine;

    public string? AccountId
    {
        get
        {
            lock (_gate)
                return _state.Online ? _state.AccountId : null;
        }
    }

    public SlotSessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public string? QueueText => Host.Scheduler?.Status is { IsRunning: true } q ? q.NotificationLine ?? q.Text : null;

    private ArmyAccount? CurrentAccount()
    {
        string? id;
        lock (_gate)
            id = _state.AccountId;
        return _accounts.Find(id);
    }

    public async Task LoginAsync(string accountId, string? server, CancellationToken cancellationToken)
    {
        if (!AccountStore.IsValidId(accountId))
            throw new EngineException("Unknown account.");
        ArmyAccount account = _accounts.Reload().Accounts.FirstOrDefault(a => a.Id == accountId)
            ?? throw new EngineException("The account is not in the account list.");
        string? password = _vault.Load(account, out VaultStatus status);
        if (password is null)
        {
            throw new EngineException(status == VaultStatus.Unavailable
                ? $"The saved password of {account.Label} cannot be read right now."
                : $"No password is saved for {account.Label}: edit the account and enter it.");
        }
        string? effectiveServer = string.IsNullOrWhiteSpace(server) ? account.Server : server;
        SetState(_ => new SlotSessionState(account.Id, effectiveServer, Online: false));
        await Engine.LoginAsync(new LoginRequest(account.Username, password, effectiveServer, Host.Settings.Current.ClientVersion), cancellationToken).ConfigureAwait(false);
        SetState(s => s with { Online = true });
    }

    /// <summary>After a restart of this process: logs back in when the slot was online before.</summary>
    public async Task<string?> ResumeAsync(CancellationToken cancellationToken = default)
    {
        SlotSessionState state = State;
        if (!state.Online || state.AccountId is null)
            return null;
        try
        {
            await LoginAsync(state.AccountId, state.Server, cancellationToken).ConfigureAwait(false);
            string? queue = Host.Scheduler?.ResumeAfterRestart() == true ? " The script queue resumes." : string.Empty;
            return $"Slot {Slot} logged back in.{queue}";
        }
        catch (EngineException e)
        {
            return $"Slot {Slot} could not log back in: {e.Message}";
        }
    }

    public string ResolveScript(string relativePath) => Layout.ResolveScript(relativePath);

    public void PrepareOptionProfile(string storage)
    {
        try
        {
            Layout.CopyOptionProfile(storage);
        }
        catch (IOException e)
        {
            throw new EngineException($"The option profile could not be copied: {e.Message}");
        }
    }

    public bool RequestRestart(string reason) => Host.RequestRestart(reason);

    private void SetState(Func<SlotSessionState, SlotSessionState> change)
    {
        lock (_gate)
        {
            _state = change(_state);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
                File.WriteAllText(_stateFile + ".tmp", JsonSerializer.Serialize(_state));
                File.Move(_stateFile + ".tmp", _stateFile, overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static SlotSessionState ReadState(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SlotSessionState>(File.ReadAllText(file)) ?? new(null, null, false) : new(null, null, false);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new(null, null, false);
        }
    }
}
