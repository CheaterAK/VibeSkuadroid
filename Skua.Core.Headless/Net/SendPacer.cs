namespace Skua.Core.Headless.Net;

/// <summary>Where an outgoing packet goes in the send queue.</summary>
public enum SendPriority
{
    /// <summary>Queued behind everything else and paced.</summary>
    Normal,

    /// <summary>Ahead of normal packets, still paced (probes, AFK clearing, logout).</summary>
    High,

    /// <summary>Ahead of everything and not paced (SmartFox handshake XML).</summary>
    Immediate,
}

/// <summary>
/// The outbound queue's ordering, pacing and overflow rules, separated from the socket so it can be tested with
/// plain timestamps.
/// </summary>
/// <remarks>
/// Game packets leave one at a time with a gap, like a person clicking; bursts make the server answer "Please slow
/// down. Last action was too soon!" and refuse the action. After such a warning the gap widens for a while. The
/// default numbers (220 ms, 500 ms for 30 s, at most 60 queued, aggro pulls merged) come from Seraph's headless
/// client (MIT, Ezzeldeen2006), which measured them against the live server.
/// </remarks>
internal sealed class SendPacer
{
    private readonly object _gate = new();
    private readonly LinkedList<OutboundItem> _queue = new();
    private readonly TimeSpan _gap;
    private readonly TimeSpan _slowGap;
    private readonly TimeSpan _slowHold;
    private readonly int _maxQueued;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private DateTimeOffset _slowUntil = DateTimeOffset.MinValue;

    public SendPacer(TimeSpan gap, TimeSpan slowGap, TimeSpan slowHold, int maxQueued)
    {
        _gap = gap;
        _slowGap = slowGap;
        _slowHold = slowHold;
        _maxQueued = Math.Max(1, maxQueued);
    }

    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    public long Dropped { get; private set; }

    public long Superseded { get; private set; }

    /// <summary>Queues a packet. Returns the packets thrown away to make room (their completions are already set).</summary>
    public IReadOnlyList<OutboundItem> Enqueue(OutboundItem item)
    {
        List<OutboundItem>? lost = null;
        lock (_gate)
        {
            // One queued aggro pull at a time: the newest replaces the older one (aggro sticks server side).
            if (item.Command == "aggroMon")
            {
                for (LinkedListNode<OutboundItem>? node = _queue.First; node is not null; node = node.Next)
                {
                    if (node.Value.Command == "aggroMon")
                    {
                        node.Value.Complete(false);
                        node.Value = item;
                        Superseded++;
                        return Array.Empty<OutboundItem>();
                    }
                }
            }

            switch (item.Priority)
            {
                case SendPriority.Immediate:
                    _queue.AddFirst(item);
                    break;

                case SendPriority.High:
                    LinkedListNode<OutboundItem>? after = null;
                    for (LinkedListNode<OutboundItem>? node = _queue.First; node is not null && node.Value.Priority != SendPriority.Normal; node = node.Next)
                        after = node;
                    if (after is null)
                        _queue.AddFirst(item);
                    else
                        _queue.AddAfter(after, item);
                    break;

                default:
                    _queue.AddLast(item);
                    break;
            }

            // Never build a backlog: filler (aggro, walking) goes first, then the oldest normal packets.
            while (_queue.Count > _maxQueued)
            {
                LinkedListNode<OutboundItem>? victim = null;
                for (LinkedListNode<OutboundItem>? node = _queue.First; node is not null; node = node.Next)
                {
                    if (node.Value.Priority == SendPriority.Normal && node.Value.IsFiller)
                    {
                        victim = node;
                        break;
                    }
                }
                if (victim is null)
                {
                    for (LinkedListNode<OutboundItem>? node = _queue.First; node is not null; node = node.Next)
                    {
                        if (node.Value.Priority == SendPriority.Normal)
                        {
                            victim = node;
                            break;
                        }
                    }
                }
                if (victim is null)
                    break; // only priority packets left: keep them
                _queue.Remove(victim);
                victim.Value.Complete(false);
                Dropped++;
                (lost ??= new()).Add(victim.Value);
            }
        }
        return lost ?? (IReadOnlyList<OutboundItem>)Array.Empty<OutboundItem>();
    }

    /// <summary>
    /// Takes the next packet if it may leave now. Otherwise returns false with how long to wait
    /// (<see cref="TimeSpan.Zero"/> when the queue is empty).
    /// </summary>
    public bool TryDequeue(DateTimeOffset now, out OutboundItem? item, out TimeSpan wait)
    {
        lock (_gate)
        {
            item = null;
            wait = TimeSpan.Zero;
            LinkedListNode<OutboundItem>? first = _queue.First;
            if (first is null)
                return false;
            if (first.Value.Paced)
            {
                TimeSpan due = (_lastWrite == DateTimeOffset.MinValue ? now : _lastWrite + CurrentGapCore(now)) - now;
                if (due > TimeSpan.Zero)
                {
                    wait = due;
                    return false;
                }
            }
            _queue.RemoveFirst();
            item = first.Value;
            return true;
        }
    }

    /// <summary>Records that a paced packet was written.</summary>
    public void MarkWritten(DateTimeOffset now)
    {
        lock (_gate) _lastWrite = now;
    }

    /// <summary>The server said "slow down": widen the gap for a while.</summary>
    public void SlowDown(DateTimeOffset now)
    {
        lock (_gate) _slowUntil = now + _slowHold;
    }

    public TimeSpan CurrentGap(DateTimeOffset now)
    {
        lock (_gate) return CurrentGapCore(now);
    }

    public bool IsSlowed(DateTimeOffset now)
    {
        lock (_gate) return now < _slowUntil;
    }

    /// <summary>Empties the queue, completing every packet as not sent.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            int n = _queue.Count;
            foreach (OutboundItem item in _queue)
                item.Complete(false);
            _queue.Clear();
            return n;
        }
    }

    private TimeSpan CurrentGapCore(DateTimeOffset now)
    {
        return now < _slowUntil ? _slowGap : _gap;
    }
}

/// <summary>One queued outgoing packet.</summary>
internal sealed class OutboundItem
{
    private readonly TaskCompletionSource<bool>? _completion;

    public OutboundItem(string text, SendPriority priority, bool wantCompletion)
    {
        Text = text;
        Priority = priority;
        Command = Protocol.Packets.CommandOf(text);
        // Only game commands are paced; the SmartFox handshake XML is not.
        Paced = priority != SendPriority.Immediate && text.StartsWith("%xt%", StringComparison.Ordinal);
        IsFiller = Command is "aggroMon" or "mv";
        if (wantCompletion)
            _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public string Text { get; }

    public SendPriority Priority { get; }

    public string? Command { get; }

    public bool Paced { get; }

    public bool IsFiller { get; }

    /// <summary>True when written to the socket, false when dropped, superseded or the connection closed.</summary>
    public Task<bool> Completion => _completion?.Task ?? Task.FromResult(true);

    public void Complete(bool written)
    {
        _completion?.TrySetResult(written);
    }
}
