using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Army;

/// <summary>The spacing rules of <see cref="LoginStagger"/>, from the desktop's own.</summary>
public sealed record StaggerOptions
{
    /// <summary>
    /// Between the starts of two logins. The desktop's "Login All Clients" waits 2 s between clients
    /// (TabbedHostWindow.MenuItem_LoginAll_Click), its account manager 1 s and its army queue 3 s between launches.
    /// </summary>
    public TimeSpan Spacing { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Random extra wait added to every spacing: Skua's relogin jitter (ScriptServers: 400-1000 ms).</summary>
    public int JitterMinMs { get; init; } = 400;

    public int JitterMaxMs { get; init; } = 1000;

    /// <summary>Attempts per account in one Start All (the first included).</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>Before a retry: Skua's ReloginTryDelay default (800 ms) times the attempt number, plus the jitter.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    public static StaggerOptions Default { get; } = new();
}

/// <summary>One login a <see cref="LoginStagger"/> runs.</summary>
/// <param name="Key">The account id (one entry per account and run).</param>
/// <param name="Label">For messages.</param>
/// <param name="Login">The login itself; an <see cref="EngineException"/> is a failure, retried when <see cref="LoginStagger.ShouldRetry"/> says so.</param>
public sealed record StaggeredLogin(string Key, string Label, Func<CancellationToken, Task> Login);

/// <summary>How one login of a staggered run ended.</summary>
public sealed record StaggerResult(string Key, string Label, bool Ok, int Attempts, string? Error);

/// <summary>
/// Never lets two accounts of this device log in at the same moment: every web/socket login of the army (Start All,
/// one account's login, the light sessions' relogins) takes its turn here, one at a time, each start at least
/// <see cref="StaggerOptions.Spacing"/> plus a random jitter after the previous start, with bounded retries. The AQW
/// client itself has no per-device rule (see the report); the server throttles fast logins and actions, and the
/// desktop spaces its logins the same way.
/// </summary>
public sealed class LoginStagger
{
    private readonly StaggerOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private DateTimeOffset? _lastStart;

    public LoginStagger(StaggerOptions? options = null, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
    {
        _options = options ?? StaggerOptions.Default;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
        _random = random ?? Random.Shared;
    }

    public StaggerOptions Options => _options;

    /// <summary>The jitter of one spacing (thread-safe).</summary>
    public TimeSpan NextJitter()
    {
        int ms;
        lock (_random)
            ms = _random.Next(_options.JitterMinMs, Math.Max(_options.JitterMinMs, _options.JitterMaxMs) + 1);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Waits for this caller's turn: until no other login runs and the spacing since the previous start has passed.
    /// Dispose the result when the login finished (success or not).
    /// </summary>
    public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastStart is DateTimeOffset last)
            {
                TimeSpan wait = last + _options.Spacing + NextJitter() - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
            _lastStart = _time.GetUtcNow();
            return new Turn(_turn);
        }
        catch
        {
            _turn.Release();
            throw;
        }
    }

    /// <summary>
    /// Runs the logins one after another through <see cref="EnterAsync"/>, retrying each failed one (after the retry
    /// delay) up to <see cref="StaggerOptions.Attempts"/> times unless <see cref="ShouldRetry"/> says the error is final.
    /// </summary>
    public async Task<IReadOnlyList<StaggerResult>> RunAsync(IEnumerable<StaggeredLogin> logins, IProgress<StaggerResult>? progress = null, CancellationToken cancellationToken = default)
    {
        List<StaggerResult> results = new();
        foreach (StaggeredLogin login in logins.DistinctBy(l => l.Key))
        {
            StaggerResult result = await RunOneAsync(login, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    /// <summary>One login through the turn, with the retries.</summary>
    public async Task<StaggerResult> RunOneAsync(StaggeredLogin login, CancellationToken cancellationToken = default)
    {
        string? error = null;
        for (int attempt = 1; attempt <= Math.Max(1, _options.Attempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1)
                await _delay(_options.RetryDelay * (attempt - 1) + NextJitter(), cancellationToken).ConfigureAwait(false);
            await using (await EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await login.Login(cancellationToken).ConfigureAwait(false);
                    return new StaggerResult(login.Key, login.Label, true, attempt, null);
                }
                catch (EngineException e)
                {
                    error = e.Message;
                    if (!ShouldRetry(e.Message))
                        return new StaggerResult(login.Key, login.Label, false, attempt, error);
                }
            }
        }
        return new StaggerResult(login.Key, login.Label, false, _options.Attempts, error);
    }

    /// <summary>
    /// Whether a failed login is worth another try now: not for refused credentials, a missing saved password, an
    /// account logged in elsewhere or a refusal the user has to fix; yes for network errors, timeouts and full servers.
    /// </summary>
    public static bool ShouldRetry(string message)
    {
        string m = message.ToLowerInvariant();
        string[] final =
        [
            "wrong password", "invalid", "refused: ", "no password", "cannot be read", "not in the account list", "unknown account",
            "banned", "locked", "logged in elsewhere", "another window", "member server", "enter the", "stop the running script",
        ];
        return !final.Any(m.Contains);
    }

    private sealed class Turn : IAsyncDisposable
    {
        private SemaphoreSlim? _turn;

        public Turn(SemaphoreSlim turn)
        {
            _turn = turn;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _turn, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
