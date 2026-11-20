using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

/// <summary>
/// The socket receive buffer's ownership and canaries: what the canary catches, that the old read pattern (a receive
/// cancelled by token, buffer released at once) lets a transport write into a released buffer while the current one
/// does not, and a connect/disconnect/probe/cancel stress run under heavy traffic with the canaries on.
/// </summary>
public class ReceiveBufferTests : IAsyncLifetime
{
    private readonly FakeAqwServer _fake = new();
    private readonly ConcurrentQueue<CanaryViolation> _violations = new();
    private readonly string _tag = "test-" + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync()
    {
        ReceiveBufferCanary.Violation += OnViolation;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        ReceiveBufferCanary.Violation -= OnViolation;
        await _fake.DisposeAsync();
    }

    // Only this test class's buffers (tests run in parallel; the canary is process-wide).
    private void OnViolation(CanaryViolation v)
    {
        if (v.Owner.Contains(_tag, StringComparison.Ordinal) || v.Owner.Contains("127.0.0.1:" + _fake.Port, StringComparison.Ordinal))
            _violations.Enqueue(v);
    }

    [Fact]
    public void ACleanReadPassesAndRecyclingRestoresThePoison()
    {
        ReceiveBuffer b = new(_tag, 256);
        Encoding.ASCII.GetBytes("{\"t\":\"xt\"}").CopyTo(b.Payload.Span);
        Assert.Null(b.CheckAfterRead(10));
        Assert.Equal("{\"t\":\"xt\"}", Encoding.ASCII.GetString(b.Received(10)));
        b.Recycle(10);
        Assert.Null(b.CheckAfterRead(0));
        Assert.Empty(_violations);
    }

    [Fact]
    public void AWritePastTheBytesReceivedIsCaught()
    {
        ReceiveBuffer b = new(_tag, 256);
        Encoding.ASCII.GetBytes("\",\"b\":{\"r\":-1").CopyTo(b.Payload.Span[100..]);
        CanaryViolation? v = b.CheckAfterRead(20);
        Assert.NotNull(v);
        Assert.Equal(100, v.Offset);
        Assert.StartsWith("\",\"b\":{", v.Sample);
        Assert.Single(_violations);
    }

    [Fact]
    public void AnOverrunIntoTheGuardBandIsCaught()
    {
        ReceiveBuffer b = new(_tag, 256);
        MemoryLeak(b)[ReceiveBuffer.GuardBytes + 256 + 3] = (byte)'x';
        CanaryViolation? v = b.CheckAfterRead(256);
        Assert.NotNull(v);
        Assert.Equal(259, v.Offset);
        Assert.Contains("guard band after", v.Where);
    }

    [Fact]
    public void AWriteAfterReleaseIsCaughtByTheSweep()
    {
        ReceiveBuffer b = new(_tag, 256);
        Memory<byte> stale = b.Payload; // what a receive still in flight would hold
        b.Release();
        Assert.Throws<ObjectDisposedException>(() => b.Payload);
        Assert.Null(b.CheckReleased());
        Encoding.ASCII.GetBytes("{\"t\":\"xt\",\"b\":{").CopyTo(stale.Span[8..]);
        ReceiveBufferCanary.SweepReleased();
        CanaryViolation v = Assert.Single(_violations);
        Assert.Equal("after release", v.Phase);
        Assert.Equal(8, v.Offset);
    }

    // The whole array, guards included, reached the way a stray native writer would (no bounds).
    private static byte[] MemoryLeak(ReceiveBuffer b)
    {
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(b.Payload, out ArraySegment<byte> seg);
        return seg.Array!;
    }

    /// <summary>
    /// The hazard the read loop was changed for, shown with the canary: a transport whose cancelled read returns at
    /// once while the receive it started keeps going (many stream wrappers cancel this way, e.g. WaitAsync). With the
    /// old pattern (pass the token, release the buffer when the read ends) packet bytes land in the released buffer;
    /// with the current one (no token: close the stream, await the read, then release) they cannot.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelledReceiveWritesIntoAReleasedBufferOnlyWithTheOldPattern(bool oldPattern)
    {
        using TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient server = await listener.AcceptTcpClientAsync();
        EarlyCancelStream stream = new(client.GetStream());

        ReceiveBuffer b = new($"{_tag} old={oldPattern}", 4096);
        using CancellationTokenSource lifetime = new();
        Task reader = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(b.Payload, oldPattern ? lifetime.Token : CancellationToken.None);
                    if (read == 0)
                        return;
                    b.CheckAfterRead(read);
                    b.Recycle(read);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                b.Release();
            }
        });

        await Task.Delay(50);
        // Close the way AqwConnection does: cancel, then (old: some time later; the window is what matters) dispose.
        lifetime.Cancel();
        if (!oldPattern)
            stream.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        // The server's next packet arrives while the cancelled receive is still pending.
        server.GetStream().Write(Encoding.UTF8.GetBytes("{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"ct\"}}}\0"));
        await Task.Delay(200);
        stream.Dispose();
        ReceiveBufferCanary.SweepReleased();

        if (oldPattern)
        {
            CanaryViolation v = Assert.Single(_violations);
            Assert.Equal("after release", v.Phase);
            Assert.StartsWith("{\"t\":\"xt\",\"b\":{", v.Sample);
        }
        else
        {
            Assert.Empty(_violations);
        }
    }

    /// <summary>
    /// Connect, probe, cancel, disconnect, server-side close and abrupt dispose, over and over, while the server floods
    /// every session with packets (plain and compressed). No receive buffer may ever be written outside a read.
    /// </summary>
    [Fact]
    public async Task ConnectDisconnectProbeCancelUnderHeavyTrafficNeverWritesOutsideARead()
    {
        int cycles = int.TryParse(Environment.GetEnvironmentVariable("SKUA_SOCKET_STRESS_CYCLES"), out int n) ? n : 30;
        using CancellationTokenSource stop = new();
        string small = new JsonObject { ["t"] = "xt", ["b"] = new JsonObject { ["r"] = -1, ["o"] = new JsonObject { ["cmd"] = "ct", ["p"] = new JsonObject { ["Hero"] = new JsonObject { ["intHP"] = 900 } } } } }.ToJsonString();
        string large = PacketCodec.Pack(new JsonObject { ["t"] = "xt", ["b"] = new JsonObject { ["r"] = -1, ["o"] = new JsonObject { ["cmd"] = "ct", ["pad"] = new string('x', 6000) } } }.ToJsonString());
        Task flood = Task.Run(async () =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                _fake.Broadcast(i++ % 4 == 0 ? large : small);
                _fake.Broadcast("%xt%uotls%-1%Hero%strFrame:Enter,intState:1%");
                if (i % 8 == 0)
                    await Task.Yield();
                if (i % 64 == 0)
                    await Task.Delay(1);
            }
        });

        Random random = new(1234);
        long before = ReceiveBufferCanary.ReleasedChecks;
        for (int i = 0; i < cycles; i++)
        {
            AqwConnection c = new(ConnectionTests.Fast());
            using CancellationTokenSource cancel = new();
            int mode = i % 5;
            if (mode == 0)
                cancel.CancelAfter(random.Next(1, 40));
            try
            {
                await c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32", cancel.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or AqwLoginException)
            {
            }
            if (c.IsReady)
            {
                for (int p = 0; p < 3; p++)
                    _ = c.ProbeAsync(TimeSpan.FromMilliseconds(random.Next(1, 200)));
                await Task.Delay(random.Next(5, 60));
            }
            switch (mode)
            {
                case 1:
                    await c.DisconnectAsync();
                    break;
                case 2:
                    _fake.LastSession?.Close();
                    await c.Closed.WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                case 3:
                    _ = c.ProbeAsync(TimeSpan.FromMilliseconds(5));
                    break; // disposed below while the probe and the reads are in flight
            }
            await c.DisposeAsync();
            Assert.Equal(ConnectionState.Closed, c.State);
        }
        stop.Cancel();
        await flood;
        // Give any receive that outlived its connection time to land, then check every quarantined buffer.
        await Task.Delay(300);
        ReceiveBufferCanary.SweepReleased();
        Assert.True(ReceiveBufferCanary.ReleasedChecks > before);
        Assert.Empty(_violations);
    }

    /// <summary>A stream whose cancelled read returns at once while the underlying receive goes on into the caller's buffer.</summary>
    private sealed class EarlyCancelStream : Stream
    {
        private readonly NetworkStream _inner;

        public EarlyCancelStream(NetworkStream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Task<int> receive = _inner.ReadAsync(buffer, CancellationToken.None).AsTask();
            return await receive.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
