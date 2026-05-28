using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using ConnectNet.Pooling;

namespace ConnectNet;

public class GzipCompressor : ICompressor
{
    public string Name => "gzip";

    public void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        using var sink = new BufferWriterStream(destination);
        using (var gzip = new GZipStream(sink, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(source.Span);
        }
    }

    public void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination, int maxBytes)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (maxBytes <= 0)
            throw new ConnectException(ConnectCode.ResourceExhausted, "decompression limit must be positive");

        using var input = AsStream(source);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        var scratch = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int n;
            while ((n = gzip.Read(scratch, 0, scratch.Length)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"decompressed size exceeds limit {maxBytes}");
                var dest = destination.GetSpan(n);
                scratch.AsSpan(0, n).CopyTo(dest);
                destination.Advance(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Wraps a <see cref="ReadOnlyMemory{T}"/> in a non-copying <see cref="Stream"/>.
    /// If the memory is backed by an array we use that directly (zero copy); otherwise
    /// a one-time copy is made. In Connect's hot path the input always originates from
    /// either <see cref="ArrayPool{T}"/> or a <c>byte[]</c>, so the array fast path applies.
    /// </summary>
    internal static Stream AsStream(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> seg) && seg.Array != null)
        {
            return new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false);
        }
        // Fallback: rare path, e.g. memory backed by a native pool. Copy once.
        var copy = memory.ToArray();
        return new MemoryStream(copy, writable: false);
    }
}
