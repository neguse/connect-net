using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using ConnectNet.Pooling;

namespace ConnectNet;

public class DeflateCompressor : ICompressor
{
    public string Name => "deflate";

    public void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        // ZLIB header: CMF=0x78 (deflate, window size 32K), FLG=0x01
        // 0x78 * 256 + 0x01 = 30721, 30721 % 31 = 0 ✓
        var header = destination.GetSpan(2);
        header[0] = 0x78;
        header[1] = 0x01;
        destination.Advance(2);

        // Raw DEFLATE data via Stream adapter
        var startedAt = 0;
        var sink = new BufferWriterStream(destination);
        using (var deflate = new DeflateStream(sink, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(source.Span);
            deflate.Flush();
            startedAt = 1; // sentinel — we cannot easily detect "wrote nothing" here, see below
        }
        sink.Dispose();

        // .NET's DeflateStream may produce 0 bytes for empty input. A valid DEFLATE stream
        // requires at least a final block, so add one if needed.
        if (source.Length == 0)
        {
            var emptyBlock = destination.GetSpan(5);
            emptyBlock[0] = 0x01; // BFINAL=1, BTYPE=00
            emptyBlock[1] = 0x00; // LEN low
            emptyBlock[2] = 0x00; // LEN high
            emptyBlock[3] = 0xFF; // NLEN low
            emptyBlock[4] = 0xFF; // NLEN high
            destination.Advance(5);
        }
        _ = startedAt;

        // Adler32 checksum (big-endian, 4 bytes trailer)
        uint checksum = Adler32(source.Span);
        var trailer = destination.GetSpan(4);
        trailer[0] = (byte)(checksum >> 24);
        trailer[1] = (byte)(checksum >> 16);
        trailer[2] = (byte)(checksum >> 8);
        trailer[3] = (byte)checksum;
        destination.Advance(4);
    }

    public void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination, int maxBytes)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (maxBytes <= 0)
            throw new ConnectException(ConnectCode.ResourceExhausted, "decompression limit must be positive");
        if (source.Length < 2)
            throw new InvalidDataException("Invalid ZLIB data");

        var span = source.Span;
        // Check if data has ZLIB header (CMF byte with method=8 for deflate)
        bool hasZlibHeader = (span[0] & 0x0F) == 8 && ((span[0] * 256 + span[1]) % 31 == 0);

        ReadOnlyMemory<byte> deflateBody = hasZlibHeader
            ? source.Slice(2)  // strip 2-byte ZLIB header; trailing Adler32 is ignored by DeflateStream EOF
            : source;

        using var input = GzipCompressor.AsStream(deflateBody);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        var scratch = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int n;
            while ((n = deflate.Read(scratch, 0, scratch.Length)) > 0)
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

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        const uint MOD = 65521;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }
}
