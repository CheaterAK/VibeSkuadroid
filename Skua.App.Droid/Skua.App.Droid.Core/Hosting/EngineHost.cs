using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Core.Hosting;

/// <summary>
/// Process-wide orchestration around the <see cref="IBotEngine"/>, shared by the UI and the Android foreground
/// service: login with "remember me", resume after a planned restart, settings applied to the engine, and the
/// memory watchdog that decides when the process should be restarted.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly ResumeStore _resume;
    private readonly TimeProvider _time;
    private readonly MemoryWatchdog _watchdog;
    private DateTimeOffset _lastMemorySample;
    private int _restartRequested;

    public EngineHost(IBotEngine engine, SettingsStore settings, CredentialVault vault, ResumeStore resume, IPlatformServices platform, TimeProvider? time = null)
    {
        Engine = engine;
        Settings = settings;
        Vault = vault;
        Platform = platform;
        _resume = resume;
        _time = time ?? TimeProvider.System;
        _watchdog = new MemoryWatchdog(WatchdogOptions(settings.Current), settings.Current.LastAutoRestartAt, _time);
        Engine.AutoReconnect = settings.Current.AutoReconnect;
        Settings.Changed += OnSettingsChanged;
        Engine.SnapshotChanged += OnSnapshot;
    }

    public IBotEngine Engine { get; }

    public SettingsStore Settings { get; }

    public CredentialVault Vault { get; }

    public IPlatformServices Platform { get; }

    /// <summary>The script queue (null until <see cref="AttachScheduler"/>; the platform head creates it with the engine).</summary>
    public ScriptScheduler? Scheduler { get; private set; }

    /// <summary>
    /// Gives the host its script queue: the host restarts the engine between queue runs when memory is over the
    /// threshold, tells the queue about every restart (so it saves where to resume) and resumes it after the restart's
    /// login. Once.
    /// </summary>
    public void AttachScheduler(ScriptScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (Scheduler is not null)
            throw new InvalidOperationException("The host already has a script queue.");
        Scheduler = scheduler;
        scheduler.RestartBetweenRuns = TryRestartBetweenQueueRuns;
        RestartStarting += (_, _) => scheduler.OnRestarting();
    }

    /// <summary>
    /// Between two queue runs (no script runs): restarts the process when memory is over the watchdog threshold and an
    /// automatic restart is allowed now (the same limits as the watchdog's, without its idle grace) and the session can
    /// be restored afterwards. Script assemblies are never unloaded on Android, so a long queue of different scripts
    /// grows memory; this gives it back between runs instead of never (the watchdog alone waits for an idle engine).
    /// </summary>
    public bool TryRestartBetweenQueueRuns(string reason)
    {
        EngineSnapshot s = Engine.Snapshot;
        if (!Platform.CanRestartProcess || s.Script.IsBusy || !CanResumeAfterRestart() || AutomaticRestartVeto?.Invoke() is not null)
            return false;
        if (!_watchdog.TryClaimRestartBetweenRuns(s.Memory.RssBytes))
            return false;
        Settings.Update(x => x with { LastAutoRestartAt = _watchdog.LastAutoRestartAt });
        return RequestRestart($"{reason}: {s.Memory.RssMb:0} MB over {Settings.Current.MemoryThresholdMb} MB");
    }

    public MemoryVerdict MemoryVerdict => _watchdog.LastVerdict;

    /// <summary>Raised when <see cref="MemoryVerdict"/> changes (background thread).</summary>
    public event EventHandler<MemoryVerdict>? MemoryVerdictChanged;

    /// <summary>Raised right before the process restarts itself (the service tears down its notification and locks).</summary>
    public event EventHandler<string>? RestartStarting;

    /// <summary>
    /// The account a password is remembered for, from the settings metadata and the presence of the encrypted entry.
    /// Nothing is decrypted, so this is cheap enough for every keystroke and every memory sample.
    /// </summary>
    public string? RememberedAccount
    {
        get
        {
            AppSettings s = Settings.Current;
            if (!s.RememberPassword || !Vault.HasStoredPassword)
                return null;
            return s.RememberedAccount ?? s.AccountName;
        }
    }

    /// <summary>A password is remembered for <paramref name="account"/> (metadata only: it may still fail to decrypt).</summary>
    public bool HasRememberedPassword(string? account) =>
        !string.IsNullOrWhiteSpace(account) && RememberedAccount is string bound
        && string.Equals(bound, account.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs in. An empty <paramref name="password"/> uses the remembered one. On success the account and server are
    /// saved, and the password is stored in (or removed from) the vault according to <paramref name="remember"/>.
    /// </summary>
    public async Task LoginAsync(string account, string password, string? server, bool remember, CancellationToken cancellationToken = default)
    {
        VaultStatus stored = VaultStatus.None;
        string? effective = string.IsNullOrEmpty(password) ? Vault.Load(account, out stored) : password;
        if (string.IsNullOrEmpty(effective))
        {
            throw new EngineException(stored == VaultStatus.Unavailable
                ? "The saved password cannot be read right now. Enter the password."
                : "Enter the password.");
        }

        await Engine.LoginAsync(new LoginRequest(account, effective, server, Settings.Current.ClientVersion), cancellationToken).ConfigureAwait(false);

        bool canRemember = remember && Vault.IsAvailable;
        if (canRemember)
        {
            try
            {
                Vault.Save(account, effective);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The session is up; failing to remember the password must not turn it into a failed login.
                canRemember = false;
                RememberFailed?.Invoke(this, e);
            }
        }
        if (!canRemember)
            Vault.Forget();
        Settings.Update(s => s with
        {
            AccountName = account,
            Server = server,
            RememberPassword = canRemember,
            RememberedAccount = canRemember ? account.Trim() : null,
        });
    }

    /// <summary>Logs out (the engine's logout also stops the script queue).</summary>
    public Task LogoutAsync(CancellationToken cancellationToken = default) => Engine.LogoutAsync(cancellationToken);

    public void ForgetPassword()
    {
        Vault.Forget();
        Settings.Update(s => s with { RememberPassword = false, RememberedAccount = null });
    }

    /// <summary>Raised when "remember me" was asked for but the password could not be stored (the login itself succeeded).</summary>
    public event EventHandler<Exception>? RememberFailed;

    /// <summary>
    /// Acts on a restart hand-over left by the previous process: logs back in when it asked for that and a password is
    /// remembered. Returns what happened, or null when there was nothing to resume. When the login could not be
    /// resumed by itself (<see cref="ResumeResult.NeedsUser"/>), the app shows its "tap to reconnect" notice.
    /// </summary>
    public async Task<ResumeResult?> ResumeAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        ResumeRequest? request = _resume.Take();
        if (request is null)
            return null;
        ResumeResult result = await ResumeLoginAsync(request, cancellationToken).ConfigureAwait(false);
        // A queue the restart interrupted goes on (it waits for the connection if the login could not be resumed).
        if (Scheduler?.ResumeAfterRestart() == true)
            result = result with { Message = result.Message + " The script queue resumes." };
        return result;
    }

    private async Task<ResumeResult> ResumeLoginAsync(ResumeRequest request, CancellationToken cancellationToken)
    {
        if (!request.ResumeLogin || string.IsNullOrWhiteSpace(request.Account))
            return new ResumeResult($"Engine restarted ({request.Reason}).", LoggedIn: false, NeedsUser: false);
        VaultStatus status = VaultStatus.None;
        string? password = Settings.Current.RememberPassword ? Vault.Load(request.Account, out status) : null;
        if (password is null)
        {
            string why = status == VaultStatus.Unavailable
                ? "the saved password could not be read right now"
                : "no password is remembered";
            return new ResumeResult($"Engine restarted ({request.Reason}). Log in again: {why}.", LoggedIn: false, NeedsUser: true);
        }
        try
        {
            await Engine.LoginAsync(new LoginRequest(request.Account, password, request.Server, Settings.Current.ClientVersion), cancellationToken).ConfigureAwait(false);
            return new ResumeResult($"Engine restarted ({request.Reason}) and logged back in.", LoggedIn: true, NeedsUser: false);
        }
        catch (EngineException e)
        {
            return new ResumeResult($"Engine restarted ({request.Reason}); logging back in failed: {e.Message}", LoggedIn: false, NeedsUser: true);
        }
    }

    /// <summary>
    /// Replaces the single-account rule of <see cref="CanResumeAfterRestart"/> (a full army slot process resumes the
    /// account it was asked to log in, from the army vault). Null: the single-account rule.
    /// </summary>
    public Func<bool>? CanResumeOverride { get; set; }

    /// <summary>
    /// A reason not to restart this process automatically now (the watchdog and the queue's restart between runs), or
    /// null. The main process vetoes while light army sessions are online in it: a restart would drop them all.
    /// </summary>
    public Func<string?>? AutomaticRestartVeto { get; set; }

    /// <summary>Whether a restart now could log back in by itself afterwards.</summary>
    public bool CanResumeAfterRestart()
    {
        if (CanResumeOverride is Func<bool> custom)
            return custom();
        EngineSnapshot s = Engine.Snapshot;
        return s.Connection is ConnectionPhase.Offline or ConnectionPhase.Failed || HasRememberedPassword(s.AccountName);
    }

    /// <summary>Restarts the process (a fresh Mono runtime returns all script assembly memory). Refused while a script runs.</summary>
    public bool RequestRestart(string reason)
    {
        if (!Platform.CanRestartProcess || Engine.Snapshot.Script.IsBusy)
            return false;
        if (Interlocked.Exchange(ref _restartRequested, 1) != 0)
            return true;
        EngineSnapshot s = Engine.Snapshot;
        bool wasOnline = s.Connection is ConnectionPhase.Online or ConnectionPhase.Reconnecting or ConnectionPhase.Connecting;
        _resume.Write(new ResumeRequest(wasOnline, s.AccountName, s.ServerName, reason, _time.GetUtcNow()));
        RestartStarting?.Invoke(this, reason);
        Platform.RestartProcess(reason, wasOnline);
        return true;
    }

    private void OnSnapshot(object? sender, EngineSnapshot snapshot)
    {
        if (snapshot.Memory.SampledAt == _lastMemorySample)
            return;
        _lastMemorySample = snapshot.Memory.SampledAt;
        MemoryVerdict before = _watchdog.LastVerdict;
        MemoryVerdict verdict = _watchdog.Evaluate(snapshot.Memory.RssBytes, snapshot.Script.State);

        if (verdict == MemoryVerdict.RestartNow)
        {
            // Never drop a session the next process could not restore.
            if (Platform.CanRestartProcess && CanResumeAfterRestart() && AutomaticRestartVeto?.Invoke() is null)
            {
                Settings.Update(s => s with { LastAutoRestartAt = _watchdog.LastAutoRestartAt });
                RequestRestart($"memory {snapshot.Memory.RssMb:0} MB over {Settings.Current.MemoryThresholdMb} MB");
            }
            verdict = MemoryVerdict.RestartPending;
        }
        if (verdict != before)
            MemoryVerdictChanged?.Invoke(this, verdict);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Engine.AutoReconnect = settings.AutoReconnect;
        _watchdog.Options = WatchdogOptions(settings);
    }

    private static MemoryWatchdogOptions WatchdogOptions(AppSettings s) => new()
    {
        ThresholdBytes = s.MemoryThresholdMb * 1024L * 1024L,
        AutoRestart = s.AutoRestartOnMemory,
    };

    public async ValueTask DisposeAsync()
    {
        Scheduler?.Dispose();
        Settings.Changed -= OnSettingsChanged;
        Engine.SnapshotChanged -= OnSnapshot;
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What <see cref="EngineHost.ResumeAfterRestartAsync"/> did.</summary>
/// <param name="Message">A line for the log.</param>
/// <param name="LoggedIn">The session is back.</param>
/// <param name="NeedsUser">A login was wanted but could not be done unattended: tell the user to reconnect.</param>
public sealed record ResumeResult(string Message, bool LoggedIn, bool NeedsUser);
