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

            payloadBuffer = length == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent((int)length);
            bytesRead = 0;
            while (bytesRead < (int)length)
            {
                var n = await stream.ReadAsync(payloadBuffer.AsMemory(bytesRead, (int)length - bytesRead), ct).ConfigureAwait(false);
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

    public static async Task<EnvelopeFrame?> ReadFrameAsync(PipeReader reader, int maxLength, CancellationToken ct = default)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "maxLength must be positive");

        // Phase 1: wait for the 5-byte header to land in the buffer.
        ReadResult result;
        byte flags;
        uint payloadLength;
        while (true)
        {
            result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= 5)
            {
                Span<byte> headerSpan = stackalloc byte[5];
                buffer.Slice(0, 5).CopyTo(headerSpan);
                flags = headerSpan[0];
                payloadLength = ((uint)headerSpan[1] << 24) | ((uint)headerSpan[2] << 16) | ((uint)headerSpan[3] << 8) | headerSpan[4];
                if (payloadLength > (uint)maxLength)
                {
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"message size {payloadLength} exceeds limit {maxLength}");
                }
                reader.AdvanceTo(buffer.GetPosition(5));
                break;
            }
            if (result.IsCompleted)
            {
                if (buffer.Length == 0)
                {
                    reader.AdvanceTo(buffer.End);
                    return null;
                }
                reader.AdvanceTo(buffer.End);
                throw new ConnectException(ConnectCode.Internal, "incomplete envelope header");
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        // Phase 2: wait for the payload.
        if (payloadLength == 0)
        {
            return new EnvelopeFrame(flags, null, 0);
        }

        var pooled = ArrayPool<byte>.Shared.Rent((int)payloadLength);
        try
        {
            while (true)
            {
                result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (buffer.Length >= payloadLength)
                {
                    var slice = buffer.Slice(0, payloadLength);
                    slice.CopyTo(pooled.AsSpan(0, (int)payloadLength));
                    reader.AdvanceTo(buffer.GetPosition((long)payloadLength));
                    var frame = new EnvelopeFrame(flags, pooled, (int)payloadLength);
                    pooled = null!; // ownership transferred to frame
                    return frame;
                }
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    throw new ConnectException(ConnectCode.Internal, "incomplete envelope data");
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            if (pooled != null) ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    /// <summary>
    /// Writes a single envelope to a <see cref="PipeWriter"/>. Used on the server response path
    /// where <c>HttpResponse.BodyWriter</c> is available.
    /// </summary>
    public static async Task WriteAsync(PipeWriter writer, byte flags, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (writer == null) throw new ArgumentNullException(nameof(writer));
        var header = writer.GetSpan(5);
        header[0] = flags;
        var length = data.Length;
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;
        writer.Advance(5);
        if (data.Length > 0)
        {
            var dest = writer.GetSpan(data.Length);
            data.Span.CopyTo(dest);
            writer.Advance(data.Length);
        }
        var flushResult = await writer.FlushAsync(ct).ConfigureAwait(false);
        if (flushResult.IsCanceled) throw new OperationCanceledException(ct);
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
