using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Platform;
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

    public MemoryVerdict MemoryVerdict => _watchdog.LastVerdict;

    /// <summary>Raised when <see cref="MemoryVerdict"/> changes (background thread).</summary>
    public event EventHandler<MemoryVerdict>? MemoryVerdictChanged;

    /// <summary>Raised right before the process restarts itself (the service tears down its notification and locks).</summary>
    public event EventHandler<string>? RestartStarting;

    /// <summary>A password is remembered for <paramref name="account"/>.</summary>
    public bool HasRememberedPassword(string? account) => Settings.Current.RememberPassword && Vault.TryLoad(account) is not null;

    /// <summary>
    /// Logs in. An empty <paramref name="password"/> uses the remembered one. On success the account and server are
    /// saved, and the password is stored in (or removed from) the vault according to <paramref name="remember"/>.
    /// </summary>
    public async Task LoginAsync(string account, string password, string? server, bool remember, CancellationToken cancellationToken = default)
    {
        string? effective = string.IsNullOrEmpty(password) ? Vault.TryLoad(account) : password;
        if (string.IsNullOrEmpty(effective))
            throw new EngineException("Enter the password.");

        await Engine.LoginAsync(new LoginRequest(account, effective, server, Settings.Current.ClientVersion), cancellationToken).ConfigureAwait(false);

        bool canRemember = remember && Vault.IsAvailable;
        if (canRemember)
            Vault.Save(account, effective);
        else
            Vault.Forget();
        Settings.Update(s => s with { AccountName = account, Server = server, RememberPassword = canRemember });
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Engine.LogoutAsync(cancellationToken);

    public void ForgetPassword()
    {
        Vault.Forget();
        Settings.Update(s => s with { RememberPassword = false });
    }

    /// <summary>
    /// Acts on a restart hand-over left by the previous process: logs back in when it asked for that and a password is
    /// remembered. Returns a line for the log/notification, or null when there was nothing to resume.
    /// </summary>
    public async Task<string?> ResumeAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        ResumeRequest? request = _resume.Take();
        if (request is null)
            return null;
        if (!request.ResumeLogin || string.IsNullOrWhiteSpace(request.Account))
            return $"Engine restarted ({request.Reason}).";
        string? password = Settings.Current.RememberPassword ? Vault.TryLoad(request.Account) : null;
        if (password is null)
            return $"Engine restarted ({request.Reason}). Log in again: no password is remembered.";
        try
        {
            await Engine.LoginAsync(new LoginRequest(request.Account, password, request.Server, Settings.Current.ClientVersion), cancellationToken).ConfigureAwait(false);
            return $"Engine restarted ({request.Reason}) and logged back in.";
        }
        catch (EngineException e)
        {
            return $"Engine restarted ({request.Reason}); logging back in failed: {e.Message}";
        }
    }

    /// <summary>Whether a restart now could log back in by itself afterwards.</summary>
    public bool CanResumeAfterRestart()
    {
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
        MemoryVerdict verdict = _watchdog.Evaluate(snapshot.Memory.RssBytes, snapshot.Script.IsBusy);

        if (verdict == MemoryVerdict.RestartNow)
        {
            // Never drop a session the next process could not restore.
            if (Platform.CanRestartProcess && CanResumeAfterRestart())
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
        Settings.Changed -= OnSettingsChanged;
        Engine.SnapshotChanged -= OnSnapshot;
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
