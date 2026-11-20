namespace Skua.App.Droid.Core.Army;

/// <summary>
/// The coordinator's end of the link to one slot process: sends encoded requests, receives encoded responses and
/// events. On Android a bound <c>Messenger</c> pair (non-exported, signature-permission service); in tests
/// <see cref="LoopbackSlotChannel"/>.
/// </summary>
public interface ISlotChannel : IAsyncDisposable
{
    /// <summary>The slot process is bound and answering.</summary>
    bool IsConnected { get; }

    /// <summary>Sends one encoded message; false when there is no link (not bound, or the process died).</summary>
    bool Send(byte[] message);

    /// <summary>An encoded response or event (any thread).</summary>
    event Action<byte[]>? Received;

    /// <summary>The link came up or went down (the slot process started, died or was unbound).</summary>
    event Action<bool>? ConnectionChanged;
}

/// <summary>An in-process channel straight to a <see cref="SlotEndpoint"/> (tests; the desktop preview).</summary>
public sealed class LoopbackSlotChannel : ISlotChannel
{
    private readonly SlotEndpoint _endpoint;
    private volatile bool _connected = true;

    public LoopbackSlotChannel(SlotEndpoint endpoint)
    {
        _endpoint = endpoint;
        _endpoint.EventReady += OnEvent;
    }

    public bool IsConnected => _connected;

    public event Action<byte[]>? Received;

    public event Action<bool>? ConnectionChanged;

    /// <summary>Tests: what the coordinator sees when the slot process dies.</summary>
    public void SimulateDeath()
    {
        _connected = false;
        ConnectionChanged?.Invoke(false);
    }

    /// <summary>Tests: raw bytes as if the slot had sent them.</summary>
    public void Inject(byte[] bytes) => Received?.Invoke(bytes);

    public bool Send(byte[] message)
    {
        if (!_connected)
            return false;
        _ = Task.Run(async () =>
        {
            byte[]? answer = await _endpoint.HandleAsync(message).ConfigureAwait(false);
            if (answer is not null && _connected)
                Received?.Invoke(answer);
        });
        return true;
    }

    private void OnEvent(byte[] bytes)
    {
        if (_connected)
            Received?.Invoke(bytes);
    }

    public ValueTask DisposeAsync()
    {
        _endpoint.EventReady -= OnEvent;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
