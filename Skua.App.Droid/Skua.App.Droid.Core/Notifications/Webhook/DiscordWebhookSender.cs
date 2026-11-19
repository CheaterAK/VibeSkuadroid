using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>How a webhook message ended.</summary>
public enum WebhookOutcome
{
    Sent,
    /// <summary>Discord refused it or kept failing (see <see cref="WebhookResult.StatusCode"/>).</summary>
    Failed,
    /// <summary>The queue was full and it was the oldest message, or the sender shut down.</summary>
    Dropped,
    /// <summary>No webhook URL is set.</summary>
    NoUrl,
}

/// <summary>The outcome of one message; <see cref="Message"/> never contains the URL.</summary>
public sealed record WebhookResult(WebhookOutcome Outcome, int? StatusCode, string Message, DateTimeOffset At);

/// <summary>Settings for <see cref="DiscordWebhookSender"/>.</summary>
public sealed class WebhookSenderOptions
{
    /// <summary>Messages waiting at most; when full the oldest is dropped (the engine never waits for Discord).</summary>
    public int QueueCapacity { get; init; } = 50;

    /// <summary>Attempts per message (429 waits and server errors count).</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>At least this long between two messages (the desktop sends at most 2 per second).</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Longest wait honoured for one 429 or an exhausted rate limit bucket.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Per request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Waits (tests replace it to record the waits).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Sends Discord webhook messages from one background queue (the desktop's threaded queue): <see cref="Enqueue"/>
/// never blocks and never touches the network, so engine, Flash and script threads only hand a payload over.
/// </summary>
/// <remarks>
/// <para>
/// Rate limits: a 429 is retried after Discord's <c>retry_after</c> (JSON body, seconds, fractional) or the
/// <c>Retry-After</c> header, at most <see cref="WebhookSenderOptions.MaxAttempts"/> times; when a reply says the
/// bucket is empty (<c>X-RateLimit-Remaining: 0</c>) the next message waits <c>X-RateLimit-Reset-After</c>; and
/// messages are at least <see cref="WebhookSenderOptions.MinGap"/> apart. Server errors and network failures are
/// retried with a growing pause; other refusals (401/404: the webhook was deleted) are not.
/// </para>
/// <para>
/// The URL is a credential: it is held in memory only (loaded from the encrypted vault), never logged, and results
/// and log lines carry status codes and exception type names only.
/// </para>
/// </remarks>
public sealed class DiscordWebhookSender : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly WebhookSenderOptions _options;
    private readonly Action<string> _log;
    private readonly Channel<Item> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private volatile DiscordWebhookUrl? _url;
    private volatile WebhookResult? _last;
    private int _pending;

    /// <param name="handler">HTTP handler (tests pass a fake); null uses the platform's default.</param>
    /// <param name="options">Limits and waits.</param>
    /// <param name="log">Receives short status lines (no URL, no payload).</param>
    public DiscordWebhookSender(HttpMessageHandler? handler = null, WebhookSenderOptions? options = null, Action<string>? log = null)
    {
        _options = options ?? new WebhookSenderOptions();
        _log = log ?? (_ => { });
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _options.RequestTimeout;
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }, dropped =>
        {
            Interlocked.Decrement(ref _pending);
            dropped.Done?.TrySetResult(Result(WebhookOutcome.Dropped, null, "Dropped: too many messages waiting."));
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Where messages go; null sends nothing (messages already queued are completed as <see cref="WebhookOutcome.NoUrl"/>).</summary>
    public DiscordWebhookUrl? Url
    {
        get => _url;
        set => _url = value;
    }

    /// <summary>Messages waiting or being sent.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>The last message's outcome.</summary>
    public WebhookResult? LastResult => _last;

    /// <summary>A message finished (background thread).</summary>
    public event EventHandler<WebhookResult>? Completed;

    /// <summary>Queues a message and returns at once (false: no URL, or the sender was disposed).</summary>
    public bool Enqueue(JsonObject payload) => Write(payload, null);

    /// <summary>Queues a message and completes when it was sent or given up (for the settings page's test button).</summary>
    public Task<WebhookResult> SendAsync(JsonObject payload)
    {
        TaskCompletionSource<WebhookResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Write(payload, done))
            done.TrySetResult(_url is null ? Result(WebhookOutcome.NoUrl, null, "No webhook URL is set.") : Result(WebhookOutcome.Dropped, null, "The sender is stopped."));
        return done.Task;
    }

    private bool Write(JsonObject payload, TaskCompletionSource<WebhookResult>? done)
    {
        if (_url is null || _stop.IsCancellationRequested)
            return false;
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(new Item(payload.ToJsonString(), done)))
            return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    private async Task RunAsync()
    {
        CancellationToken token = _stop.Token;
        TimeSpan wait = TimeSpan.Zero;
        try
        {
            await foreach (Item item in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                WebhookResult result;
                try
                {
                    if (wait > TimeSpan.Zero)
                        await _options.Delay(wait, token).ConfigureAwait(false);
                    (result, TimeSpan bucketWait) = await SendWithRetriesAsync(item.Json, token).ConfigureAwait(false);
                    wait = bucketWait > _options.MinGap ? bucketWait : _options.MinGap;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    item.Done?.TrySetResult(Result(WebhookOutcome.Dropped, null, "The sender stopped."));
                    throw;
                }
                Interlocked.Decrement(ref _pending);
                _last = result;
                item.Done?.TrySetResult(result);
                try
                {
                    Completed?.Invoke(this, result);
                }
                catch (Exception)
                {
                    // A listener's failure must not stop the queue.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        // Whatever is still queued will not be sent.
        while (_queue.Reader.TryRead(out Item? left))
            left.Done?.TrySetResult(Result(WebhookOutcome.Dropped, null, "The sender stopped."));
    }

    /// <summary>One message, with retries. Returns the outcome and how long the next message must wait.</summary>
    private async Task<(WebhookResult Result, TimeSpan NextWait)> SendWithRetriesAsync(string json, CancellationToken token)
    {
        int? lastStatus = null;
        for (int attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
        {
            DiscordWebhookUrl? url = _url;
            if (url is null)
                return (Result(WebhookOutcome.NoUrl, null, "No webhook URL is set."), TimeSpan.Zero);
            TimeSpan retryIn;
            try
            {
                using StringContent content = new(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.PostAsync(url.Uri, content, token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                lastStatus = status;
                if (response.IsSuccessStatusCode)
                    return (Result(WebhookOutcome.Sent, status, "Sent."), BucketWait(response));
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    string body = await ReadSmallAsync(response, token).ConfigureAwait(false);
                    retryIn = Clamp(RetryAfter(response, body) ?? TimeSpan.FromSeconds(attempt));
                    _log($"Discord webhook: rate limited (429), retrying in {retryIn.TotalSeconds:0.##} s.");
                }
                else if (status >= 500)
                {
                    retryIn = Clamp(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
                    _log($"Discord webhook: server error {status}, retrying.");
                }
                else
                {
                    string why = status switch
                    {
                        401 or 403 => "Discord refused the webhook (it may have been deleted or its token reset).",
                        404 => "Discord does not know this webhook (it may have been deleted).",
                        400 => "Discord refused the message (400).",
                        _ => $"Discord answered {status}.",
                    };
                    _log($"Discord webhook: {status}, not retried.");
                    return (Result(WebhookOutcome.Failed, status, why), TimeSpan.Zero);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !token.IsCancellationRequested)
            {
                // Type only: messages of network exceptions can quote the request.
                retryIn = Clamp(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
                _log($"Discord webhook: {e.GetType().Name}, retrying.");
            }
            if (attempt < _options.MaxAttempts)
                await _options.Delay(retryIn, token).ConfigureAwait(false);
        }
        return (Result(WebhookOutcome.Failed, lastStatus, lastStatus == 429
            ? "Still rate limited by Discord after several attempts."
            : "Could not reach Discord after several attempts."), TimeSpan.Zero);
    }

    /// <summary>Discord's retry_after (JSON, seconds) first, then the Retry-After header, then X-RateLimit-Reset-After.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, string body)
    {
        try
        {
            if (body.Length > 0 && JsonNode.Parse(body) is JsonObject o && o["retry_after"] is JsonValue v && v.TryGetValue(out double seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
        }
        catch (JsonException)
        {
        }
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is TimeSpan delta)
                return delta;
            if (header.Date is DateTimeOffset date)
                return date - DateTimeOffset.UtcNow;
        }
        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? raw)
            && double.TryParse(raw.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            return TimeSpan.FromSeconds(s);
        return Header(response, "X-RateLimit-Reset-After");
    }

    /// <summary>When the reply says the bucket is empty, how long until it refills.</summary>
    private TimeSpan BucketWait(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
            && remaining.FirstOrDefault() == "0"
            && Header(response, "X-RateLimit-Reset-After") is TimeSpan reset)
            return Clamp(reset);
        return TimeSpan.Zero;
    }

    private static TimeSpan? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t > _options.MaxRetryWait ? _options.MaxRetryWait : t;

    private static async Task<string> ReadSmallAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return body.Length > 4096 ? string.Empty : body;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    private static WebhookResult Result(WebhookOutcome outcome, int? status, string message) => new(outcome, status, message, DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down.
        }
        _http.Dispose();
        _stop.Dispose();
    }

    private sealed record Item(string Json, TaskCompletionSource<WebhookResult>? Done);
}
