using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class EnvelopeTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, data);
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Equal(0x00, result!.Value.flags);
        Assert.Equal(data, result.Value.data);
    }

    [Fact]
    public async Task WriteAndRead_EndStreamFlag()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("{\"metadata\":{}}");
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, data);
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, result!.Value.flags);
    }

    [Fact]
    public async Task WriteAndRead_EmptyData()
    {
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, System.Array.Empty<byte>());
        stream.Position = 0;
        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Empty(result!.Value.data);
    }

    [Fact]
    public async Task Read_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var result = await Envelope.ReadAsync(stream);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_MultipleMessages()
    {
        using var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, new byte[] { 1 });
        await Envelope.WriteAsync(stream, 0x00, new byte[] { 2 });
        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, new byte[] { 3 });
        stream.Position = 0;

        var r1 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 1 }, r1!.Value.data);
        var r2 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 2 }, r2!.Value.data);
        var r3 = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, r3!.Value.flags);
        var r4 = await Envelope.ReadAsync(stream);
        Assert.Null(r4);
    }

    [Fact]
    public async Task PipeWrite_ReaderCompleted_ThrowsOperationCanceled()
    {
        var pipe = new Pipe();
        pipe.Reader.Complete();
        await Assert.ThrowsAsync<System.OperationCanceledException>(
            () => Envelope.WriteAsync(pipe.Writer, 0x00, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task EnvelopeHeader_BigEndian()
    {
        var stream = new MemoryStream();
        await Envelope.WriteAsync(stream, 0x00, new byte[256]);
        var bytes = stream.ToArray();
        Assert.Equal(0x00, bytes[0]); // flags
        Assert.Equal(0x00, bytes[1]); // length MSB
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x01, bytes[3]);
        Assert.Equal(0x00, bytes[4]); // length LSB
        Assert.Equal(261, bytes.Length); // 5 header + 256 data
    }

    // --- the length prefix is a claim, not evidence: buffers grow with the data ---

    /// <summary>Serves a fixed script of bytes and records the size of every read request.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        public RecordingStream(byte[] data) => _data = data;
        public System.Collections.Generic.List<int> RequestedSizes { get; } = new();

        public override ValueTask<int> ReadAsync(System.Memory<byte> buffer, System.Threading.CancellationToken ct = default)
        {
            RequestedSizes.Add(buffer.Length);
            var remaining = _data.Length - _position;
            if (remaining <= 0) return new ValueTask<int>(0);
            var n = System.Math.Min(buffer.Length, remaining);
            _data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return new ValueTask<int>(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct)
            => ReadAsync(new System.Memory<byte>(buffer, offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new System.NotSupportedException();
        public override long Position
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
        public override void SetLength(long value) => throw new System.NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
    }

    private static byte[] Header(uint length, byte flags = 0x00) => new byte[]
    {
        flags,
        (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
    };

    [Fact]
    public async Task StreamRead_HugeDeclaredLength_DoesNotAskForTheWholePayloadUpFront()
    {
        // A 5-byte header claiming 4 MiB, backed by no payload at all.
        var stream = new RecordingStream(Header(4 * 1024 * 1024));

        await Assert.ThrowsAsync<ConnectException>(
            () => Envelope.ReadFrameAsync(stream, 4 * 1024 * 1024));

        Assert.All(stream.RequestedSizes, size => Assert.True(
            size <= 64 * 1024, $"read requested {size} bytes before the payload had arrived"));
    }

    [Fact]
    public async Task StreamRead_PayloadLargerThanTheInitialChunk_RoundTrips()
    {
        // Larger than the initial rental, so the buffer has to grow as data arrives.
        var payload = new byte[300 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        using var written = new MemoryStream();
        await Envelope.WriteAsync(written, 0x00, payload);
        var stream = new RecordingStream(written.ToArray());

        var frame = await Envelope.ReadFrameAsync(stream, 4 * 1024 * 1024);
        Assert.NotNull(frame);
        using (var f = frame!.Value)
        {
            Assert.Equal(payload.Length, f.Data.Length);
            Assert.True(f.Data.Span.SequenceEqual(payload));
        }
    }

    [Fact]
    public async Task PipeRead_PayloadArrivingInPieces_RoundTrips()
    {
        var payload = new byte[300 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var pipe = new Pipe();
        var read = Envelope.ReadFrameAsync(pipe.Reader, 4 * 1024 * 1024);

        await pipe.Writer.WriteAsync(Header((uint)payload.Length));
        for (int offset = 0; offset < payload.Length; offset += 4096)
        {
            var n = System.Math.Min(4096, payload.Length - offset);
            await pipe.Writer.WriteAsync(new System.ReadOnlyMemory<byte>(payload, offset, n));
        }

        var frame = await read;
        Assert.NotNull(frame);
        using (var f = frame!.Value)
        {
            Assert.Equal(payload.Length, f.Data.Length);
            Assert.True(f.Data.Span.SequenceEqual(payload));
        }
    }
}
