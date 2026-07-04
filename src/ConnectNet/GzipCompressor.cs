using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using ConnectNet.Pooling;

namespace ConnectNet;

public class GzipCompressor : ICompressor
{
    public string Name => "gzip";

    /// <summary>
    /// Canonical gzip stream for empty input (RFC 1952): 10-byte header (MTIME=0, XFL=0,
    /// OS=unknown), empty fixed-Huffman DEFLATE block (03 00), CRC32=0 and ISIZE=0 trailer.
    /// .NET's GZipStream emits nothing at all for empty input, which is not a valid stream.
    /// </summary>
    private static readonly byte[] EmptyGzipStream =
    {
        0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF,
        0x03, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    public void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (source.Length == 0)
        {
            var span = destination.GetSpan(EmptyGzipStream.Length);
            EmptyGzipStream.CopyTo(span);
            destination.Advance(EmptyGzipStream.Length);
            return;
        }
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
            uint crc = InitialCrc32;
            int n;
            while ((n = gzip.Read(scratch, 0, scratch.Length)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"decompressed size exceeds limit {maxBytes}");
                crc = UpdateCrc32(crc, scratch.AsSpan(0, n));
                var dest = destination.GetSpan(n);
                scratch.AsSpan(0, n).CopyTo(dest);
                destination.Advance(n);
            }

            // GZipStream reports EOF instead of failing when the input is cut off
            // mid-stream, so a truncated payload would otherwise pass through silently.
            // Verify the trailer (CRC32 + ISIZE, RFC 1952) against what we produced.
            var span = source.Span;
            if (span.Length < 18) // 10-byte header + 8-byte trailer
                throw new ConnectException(ConnectCode.InvalidArgument, "gzip: truncated stream");
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(span.Length - 8, 4));
            uint expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(span.Length - 4, 4));
            if (expectedCrc != (crc ^ InitialCrc32) || expectedSize != (uint)total)
                throw new ConnectException(ConnectCode.InvalidArgument, "gzip: truncated or corrupted stream (trailer mismatch)");
        }
        catch (InvalidDataException ex)
        {
            // Malformed header, corrupt deflate data, etc. Connect maps decompression
            // failures to invalid_argument (connect-go: protocol errorf on decompress).
            throw new ConnectException(ConnectCode.InvalidArgument, $"gzip: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private const uint InitialCrc32 = 0xFFFFFFFFu;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// Streaming CRC32 (RFC 1952). Seed with <see cref="InitialCrc32"/> and XOR the
    /// running value with it again once all data has been fed in.
    /// </summary>
    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
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
