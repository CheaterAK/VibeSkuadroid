using System.Text;

namespace Skua.Core.Headless.Net;

/// <summary>
/// The receive buffer of one socket, owned for the socket's whole life and checked for writes nobody should make.
/// </summary>
/// <remarks>
/// <para>
/// The array is allocated once per connection (never pooled, never shared, never resized) and handed only to that
/// connection's single reader. Every byte the reader does not currently hold as received data carries a poison value,
/// with guard bands before and after the payload, so a write that does not come from the read being awaited shows up:
/// </para>
/// <list type="bullet">
/// <item>after each read, the guard bands and the part of the payload past the bytes received must still be poison
/// (an overrun, a read reporting fewer bytes than it wrote, or a second writer);</item>
/// <item>after the reader has stopped (its last read completed), the whole array is poisoned and checked again later
/// (<see cref="ReceiveBufferCanary.SweepReleased"/>): a receive still in flight after its connection closed would
/// change it.</item>
/// </list>
/// <para>
/// The checks are vectorised scans of at most the buffer's size per read, cheap enough to stay on in Release. A
/// violation is reported once per buffer through <see cref="ReceiveBufferCanary.Violation"/>.
/// </para>
/// </remarks>
public sealed class ReceiveBuffer
{
    /// <summary>Bytes of poison before and after the payload.</summary>
    public const int GuardBytes = 64;

    /// <summary>The value of every byte not holding received data.</summary>
    public const byte Poison = 0xA5;

    private readonly byte[] _array;
    private readonly int _capacity;
    private int _released;
    private int _reported;

    public ReceiveBuffer(string owner, int capacity = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Owner = owner;
        _capacity = capacity;
        _array = new byte[capacity + 2 * GuardBytes];
        _array.AsSpan().Fill(Poison);
        ReceiveBufferCanary.SweepReleased();
    }

    /// <summary>Who owns it, for reports (e.g. the server's name).</summary>
    public string Owner { get; }

    public int Capacity => _capacity;

    public bool IsReleased => Volatile.Read(ref _released) == 1;

    /// <summary>When <see cref="Release"/> ran.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>The bytes a read may fill. Only the owning reader may use it, and only until <see cref="Release"/>.</summary>
    public Memory<byte> Payload
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsReleased, this);
            return _array.AsMemory(GuardBytes, _capacity);
        }
    }

    /// <summary>The bytes a completed read of <paramref name="count"/> bytes produced.</summary>
    public ReadOnlySpan<byte> Received(int count)
    {
        return _array.AsSpan(GuardBytes, count);
    }

    /// <summary>
    /// After a read of <paramref name="count"/> bytes: the guard bands and the payload past <paramref name="count"/>
    /// must be untouched. Returns the violation (also reported), or null.
    /// </summary>
    public CanaryViolation? CheckAfterRead(int count)
    {
        if (!ReceiveBufferCanary.Enabled)
            return null;
        if ((uint)count > (uint)_capacity)
            return Report("read", GuardBytes, 0, $"the read reported {count} bytes into a {_capacity} byte buffer");
        CanaryViolation? v = Find("read", 0, GuardBytes, "the guard band before the payload");
        v ??= Find("read", GuardBytes + count, _capacity - count, "the payload past the bytes received");
        v ??= Find("read", GuardBytes + _capacity, GuardBytes, "the guard band after the payload");
        return v;
    }

    /// <summary>The reader is done with the <paramref name="count"/> bytes of the last read: poison them again.</summary>
    public void Recycle(int count)
    {
        if (ReceiveBufferCanary.Enabled)
            _array.AsSpan(GuardBytes, Math.Clamp(count, 0, _capacity)).Fill(Poison);
    }

    /// <summary>
    /// The reader has stopped and no read on the buffer is pending any more: poison everything and hand the buffer to
    /// the quarantine, where later sweeps check that nothing writes into it.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        ReleasedAt = DateTimeOffset.UtcNow;
        if (!ReceiveBufferCanary.Enabled)
            return;
        _array.AsSpan().Fill(Poison);
        ReceiveBufferCanary.Quarantine(this);
    }

    /// <summary>After <see cref="Release"/>: the whole array must still be poison.</summary>
    public CanaryViolation? CheckReleased()
    {
        return IsReleased && ReceiveBufferCanary.Enabled
            ? Find("after release", 0, _array.Length, "the buffer after its connection closed")
            : null;
    }

    private CanaryViolation? Find(string phase, int start, int length, string where)
    {
        if (length <= 0)
            return null;
        int at = _array.AsSpan(start, length).IndexOfAnyExcept(Poison);
        if (at < 0)
            return null;
        int offset = start + at;
        int end = offset;
        while (end < start + length && _array[end] != Poison)
            end++;
        // The damaged run can contain poison-valued bytes; this is the first run, enough to identify the writer.
        return Report(phase, offset, end - offset, where);
    }

    private CanaryViolation? Report(string phase, int offset, int run, string where)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1)
            return null;
        int sampleLength = Math.Min(48, Math.Max(0, _array.Length - offset));
        CanaryViolation v = new(Owner, phase, offset - GuardBytes, run, where, Printable(_array.AsSpan(offset, sampleLength)), DateTimeOffset.UtcNow);
        ReceiveBufferCanary.Report(v);
        return v;
    }

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        StringBuilder sb = new(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        return sb.ToString();
    }
}

/// <summary>A write into a <see cref="ReceiveBuffer"/> that no read accounts for.</summary>
/// <param name="Offset">Relative to the payload start (negative: in the guard band before it).</param>
/// <param name="Length">Length of the first damaged run.</param>
/// <param name="Sample">The damaged bytes as printable ASCII ('.' for others), at most 48.</param>
public sealed record CanaryViolation(string Owner, string Phase, int Offset, int Length, string Where, string Sample, DateTimeOffset At)
{
    public override string ToString()
    {
        return FormattableString.Invariant($"Receive buffer of {Owner} written outside a read ({Phase}): {Length} byte(s) at payload offset {Offset}, in {Where}; bytes: \"{Sample}\"");
    }
}

/// <summary>Process-wide switch, reports and quarantine of <see cref="ReceiveBuffer"/> canaries.</summary>
public static class ReceiveBufferCanary
{
    /// <summary>How many released buffers are kept for checking (each is the receive buffer of one past connection).</summary>
    public const int QuarantineSize = 4;

    /// <summary>How long a released buffer stays in quarantine.</summary>
    public static readonly TimeSpan QuarantineTime = TimeSpan.FromMinutes(2);

    private static readonly object s_gate = new();
    private static readonly List<ReceiveBuffer> s_quarantine = new();
    private static long s_violations;
    private static long s_checks;

    /// <summary>On by default (also in Release). Off: no poisoning and no checks.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Raised on the thread that found it, once per buffer. Subscribers must not throw.</summary>
    public static event Action<CanaryViolation>? Violation;

    public static long Violations => Interlocked.Read(ref s_violations);

    /// <summary>Quarantine sweeps run so far.</summary>
    public static long ReleasedChecks => Interlocked.Read(ref s_checks);

    /// <summary>
    /// Checks every quarantined buffer and drops those past <see cref="QuarantineTime"/>. Runs whenever a buffer is
    /// created or a connection is disposed; the app also calls it from its heartbeat.
    /// </summary>
    public static int SweepReleased()
    {
        ReceiveBuffer[] buffers;
        lock (s_gate)
        {
            if (s_quarantine.Count == 0)
                return 0;
            buffers = s_quarantine.ToArray();
        }
        Interlocked.Increment(ref s_checks);
        int found = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ReceiveBuffer b in buffers)
        {
            if (b.CheckReleased() is not null)
                found++;
        }
        lock (s_gate)
            s_quarantine.RemoveAll(b => b.ReleasedAt is { } at && now - at > QuarantineTime);
        return found;
    }

    internal static void Quarantine(ReceiveBuffer buffer)
    {
        lock (s_gate)
        {
            s_quarantine.Add(buffer);
            while (s_quarantine.Count > QuarantineSize)
                s_quarantine.RemoveAt(0);
        }
    }

    internal static void Report(CanaryViolation violation)
    {
        Interlocked.Increment(ref s_violations);
        Action<CanaryViolation>? handler = Violation;
        if (handler is null)
            return;
        foreach (Action<CanaryViolation> single in handler.GetInvocationList().Cast<Action<CanaryViolation>>())
        {
            try
            {
                single(violation);
            }
            catch (Exception)
            {
                // A diagnostics subscriber must never break the reader.
            }
        }
    }
}
