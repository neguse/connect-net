using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectNet;

public static class Envelope
{
    public const byte FlagCompressed = 0x01;
    public const byte FlagEndStream = 0x02;

    /// <summary>
    /// Default upper bound applied when callers do not specify one. Chosen to be large enough
    /// to allow legitimate use while preventing a single 5-byte header from forcing
    /// near-2GiB pre-allocation. Callers handling untrusted input should pass a tighter limit.
    /// </summary>
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Size of the first payload rental when reading from a <see cref="Stream"/>. The length
    /// prefix is the peer's claim about what it is going to send, not evidence that it has:
    /// the first rental is capped at this size, so a 5-byte header cannot pin a rental the
    /// size of the declared message. Only once the peer has actually filled it is the
    /// declared length trusted to size the full buffer.
    /// </summary>
    private const int InitialPayloadChunk = 64 * 1024;

    public static Task WriteAsync(Stream stream, byte flags, byte[] data, CancellationToken ct = default)
        => WriteAsync(stream, flags, new ReadOnlyMemory<byte>(data ?? throw new ArgumentNullException(nameof(data))), ct);

    public static async Task WriteAsync(Stream stream, byte flags, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var headerBuffer = ArrayPool<byte>.Shared.Rent(5);
        try
        {
            headerBuffer[0] = flags;
            var length = data.Length;
            headerBuffer[1] = (byte)(length >> 24);
            headerBuffer[2] = (byte)(length >> 16);
            headerBuffer[3] = (byte)(length >> 8);
            headerBuffer[4] = (byte)length;
            await stream.WriteAsync(headerBuffer.AsMemory(0, 5), ct).ConfigureAwait(false);
            if (data.Length > 0)
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    /// <summary>
    /// Reads a single envelope from <paramref name="stream"/>. The returned frame owns a
    /// buffer rented from <see cref="ArrayPool{T}.Shared"/>; the caller MUST dispose it.
    /// </summary>
    public static Task<EnvelopeFrame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
        => ReadFrameAsync(stream, DefaultMaxMessageBytes, ct);

    public static async Task<EnvelopeFrame?> ReadFrameAsync(Stream stream, int maxLength, CancellationToken ct = default)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must be positive");

        var headerBuffer = ArrayPool<byte>.Shared.Rent(5);
        byte[]? payloadBuffer = null;
        try
        {
            var bytesRead = 0;
            while (bytesRead < 5)
            {
                var n = await stream.ReadAsync(headerBuffer.AsMemory(bytesRead, 5 - bytesRead), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (bytesRead == 0) return null;
                    throw new ConnectException(ConnectCode.Internal, "incomplete envelope header");
                }
                bytesRead += n;
            }
            var flags = headerBuffer[0];
            var length = ((uint)headerBuffer[1] << 24) | ((uint)headerBuffer[2] << 16) | ((uint)headerBuffer[3] << 8) | headerBuffer[4];
            if (length > (uint)maxLength)
                throw new ConnectException(
                    ConnectCode.ResourceExhausted,
                    $"message size {length} exceeds limit {maxLength}");

            var capacity = (int)Math.Min(length, InitialPayloadChunk);
            payloadBuffer = length == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(capacity);
            bytesRead = 0;
            while (bytesRead < (int)length)
            {
                if (bytesRead == capacity)
                {
                    // The peer has filled the capped first rental with real bytes, so the
                    // declared length is no longer pure speculation: grow straight to it,
                    // paying the copy once instead of once per doubling.
                    capacity = (int)length;
                    var grown = ArrayPool<byte>.Shared.Rent(capacity);
                    Buffer.BlockCopy(payloadBuffer, 0, grown, 0, bytesRead);
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                    payloadBuffer = grown;
                }
                var n = await stream.ReadAsync(payloadBuffer.AsMemory(bytesRead, capacity - bytesRead), ct).ConfigureAwait(false);
                if (n == 0)
                    throw new ConnectException(ConnectCode.Internal, "incomplete envelope data");
                bytesRead += n;
            }

            // Ownership transferred to the frame; suppress pool-return in finally.
            var ownedBuffer = length == 0 ? null : payloadBuffer;
            payloadBuffer = null;
            return new EnvelopeFrame(flags, ownedBuffer, (int)length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
            if (payloadBuffer != null && payloadBuffer.Length > 0)
                ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
    }

    /// <summary>
    /// Reads a single envelope from a <see cref="PipeReader"/>. The payload is copied out of the
    /// pipeline's internal buffers into a freshly rented pool buffer, then handed off via
    /// <see cref="EnvelopeFrame"/>. This avoids the per-envelope Stream-reader allocation and
    /// lets ASP.NET Core / Kestrel feed us their native PipeReader directly.
    /// </summary>
    public static Task<EnvelopeFrame?> ReadFrameAsync(PipeReader reader, CancellationToken ct = default)
        => ReadFrameAsync(reader, DefaultMaxMessageBytes, ct);

    public static Task<EnvelopeFrame?> ReadFrameAsync(PipeReader reader, int maxLength, CancellationToken ct = default)
        => ReadFrameValueAsync(reader, maxLength, ct).AsTask();

    // A ValueTask keeps a frame already sitting in the pipe from allocating a Task per message.
    internal static async ValueTask<EnvelopeFrame?> ReadFrameValueAsync(PipeReader reader, int maxLength, CancellationToken ct)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must be positive");

        // Nothing is consumed or rented until the whole frame is in the pipeline's own buffers:
        // the length prefix alone must not size an allocation.
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= 5)
            {
                Span<byte> headerSpan = stackalloc byte[5];
                buffer.Slice(0, 5).CopyTo(headerSpan);
                var flags = headerSpan[0];
                var payloadLength = ((uint)headerSpan[1] << 24) | ((uint)headerSpan[2] << 16) | ((uint)headerSpan[3] << 8) | headerSpan[4];
                if (payloadLength > (uint)maxLength)
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"message size {payloadLength} exceeds limit {maxLength}");
                }
                if (buffer.Length >= 5 + payloadLength)
                {
                    if (payloadLength == 0)
                    {
                        reader.AdvanceTo(buffer.GetPosition(5));
                        return new EnvelopeFrame(flags, null, 0);
                    }
                    var pooled = ArrayPool<byte>.Shared.Rent((int)payloadLength);
                    buffer.Slice(5, payloadLength).CopyTo(pooled);
                    reader.AdvanceTo(buffer.GetPosition(5 + payloadLength));
                    return new EnvelopeFrame(flags, pooled, (int)payloadLength);
                }
            }
            if (result.IsCompleted)
            {
                var length = buffer.Length;
                reader.AdvanceTo(buffer.End);
                if (length == 0) return null;
                throw new ConnectException(ConnectCode.Internal, length < 5 ? "incomplete envelope header" : "incomplete envelope data");
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Writes a single envelope to a <see cref="PipeWriter"/>. Used on the server response path
    /// where <c>HttpResponse.BodyWriter</c> is available.
    /// </summary>
    public static async Task WriteAsync(PipeWriter writer, byte flags, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (writer == null) throw new ArgumentNullException(nameof(writer));
        Write(writer, flags, data.Span);
        await FlushAsync(writer, ct).ConfigureAwait(false);
    }

    internal static void Write(IBufferWriter<byte> writer, byte flags, ReadOnlySpan<byte> data)
    {
        var frame = writer.GetSpan(5 + data.Length);
        FormatHeader(frame, flags, data.Length);
        data.CopyTo(frame.Slice(5));
        writer.Advance(5 + data.Length);
    }

    internal static void WriteHeader(IBufferWriter<byte> writer, byte flags, int length)
    {
        FormatHeader(writer.GetSpan(5), flags, length);
        writer.Advance(5);
    }

    private static void FormatHeader(Span<byte> header, byte flags, int length)
    {
        header[0] = flags;
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;
    }

    internal static Task FlushAsync(PipeWriter writer, CancellationToken ct)
    {
        var flush = writer.FlushAsync(ct);
        if (!flush.IsCompletedSuccessfully) return AwaitFlushAsync(flush, ct);
        ThrowIfStopped(flush.Result, ct);
        return Task.CompletedTask;
    }

    private static async Task AwaitFlushAsync(ValueTask<FlushResult> flush, CancellationToken ct)
        => ThrowIfStopped(await flush.ConfigureAwait(false), ct);

    private static void ThrowIfStopped(FlushResult flushResult, CancellationToken ct)
    {
        if (flushResult.IsCanceled) throw new OperationCanceledException(ct);
        // The reader completed (e.g. the peer disconnected): nothing will consume further
        // envelopes, so stop the producer instead of generating frames into the void.
        if (flushResult.IsCompleted) throw new OperationCanceledException("envelope reader completed; no further envelopes can be written");
    }

    /// <summary>
    /// Legacy wrapper that materializes the envelope payload as a fresh <c>byte[]</c>.
    /// New code should use <see cref="ReadFrameAsync(Stream, int, CancellationToken)"/>
    /// to keep the borrowed buffer.
    /// </summary>
    public static Task<(byte flags, byte[] data)?> ReadAsync(Stream stream, CancellationToken ct = default)
        => ReadAsync(stream, DefaultMaxMessageBytes, ct);

    public static async Task<(byte flags, byte[] data)?> ReadAsync(Stream stream, int maxLength, CancellationToken ct = default)
    {
        var frameNullable = await ReadFrameAsync(stream, maxLength, ct).ConfigureAwait(false);
        if (frameNullable == null) return null;
        using var frame = frameNullable.Value;
        return (frame.Flags, frame.Data.ToArray());
    }
}
