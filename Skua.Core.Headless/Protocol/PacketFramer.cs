namespace Skua.Core.Headless.Protocol;

/// <summary>
/// Splits a SmartFox byte stream into messages. Every message on the AQW socket, in both directions, ends with a
/// single NUL byte. Framing works on bytes, not characters, so a multi-byte UTF-8 character split between two TCP
/// reads stays whole: a frame is only decoded once its terminator has arrived.
/// </summary>
/// <remarks>Not thread safe: one framer per socket, fed by its single reader loop.</remarks>
public sealed class PacketFramer
{
    /// <summary>Generous local safety limit (matches the 16 MB used by CaptureProxy), not a server limit.</summary>
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    private byte[] _buffer;
    private int _count;

    public PacketFramer(int maxFrameBytes = DefaultMaxFrameBytes)
    {
        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        MaxFrameBytes = maxFrameBytes;
        _buffer = new byte[4096];
    }

    /// <summary>The largest message accepted before the stream is considered broken.</summary>
    public int MaxFrameBytes { get; }

    /// <summary>Bytes received after the last terminator (a message still in flight).</summary>
    public int PendingBytes => _count;

    /// <summary>Empty messages seen (two NULs in a row). Counted so they are observable, otherwise ignored.</summary>
    public long EmptyFrames { get; private set; }

    /// <summary>
    /// Adds received bytes and returns every message they complete, in order, without the terminator.
    /// </summary>
    /// <exception cref="FrameTooLargeException">An unterminated message grew past <see cref="MaxFrameBytes"/>.</exception>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        List<byte[]>? frames = null;
        while (!data.IsEmpty)
        {
            int nul = data.IndexOf((byte)0);
            if (nul < 0)
            {
                Append(data);
                break;
            }

            ReadOnlySpan<byte> tail = data[..nul];
            if (_count == 0 && tail.IsEmpty)
            {
                EmptyFrames++;
            }
            else
            {
                byte[] frame;
                if (_count == 0)
                {
                    frame = tail.ToArray();
                }
                else
                {
                    CheckSize(_count + tail.Length);
                    frame = new byte[_count + tail.Length];
                    _buffer.AsSpan(0, _count).CopyTo(frame);
                    tail.CopyTo(frame.AsSpan(_count));
                    _count = 0;
                }
                if (frame.Length > MaxFrameBytes)
                    throw new FrameTooLargeException(frame.Length, MaxFrameBytes);
                (frames ??= new()).Add(frame);
            }
            data = data[(nul + 1)..];
        }
        return frames ?? (IReadOnlyList<byte[]>)Array.Empty<byte[]>();
    }

    /// <summary>Forgets any partial message (used when a connection is replaced).</summary>
    public void Reset()
    {
        _count = 0;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        CheckSize(_count + data.Length);
        if (_count + data.Length > _buffer.Length)
        {
            int size = Math.Max(_buffer.Length * 2, _count + data.Length);
            Array.Resize(ref _buffer, Math.Min(size, Math.Max(MaxFrameBytes, _count + data.Length)));
        }
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    private void CheckSize(int size)
    {
        if (size > MaxFrameBytes)
        {
            _count = 0;
            throw new FrameTooLargeException(size, MaxFrameBytes);
        }
    }
}

/// <summary>A message exceeded the framer's safety limit; the stream can no longer be trusted.</summary>
public sealed class FrameTooLargeException : IOException
{
    public FrameTooLargeException(long size, int limit)
        : base($"A message of at least {size} bytes exceeds the {limit} byte limit.")
    {
        Size = size;
        Limit = limit;
    }

    public long Size { get; }

    public int Limit { get; }
}
