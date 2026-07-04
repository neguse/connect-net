using System;
using System.Buffers;
using System.Buffers.Binary;
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
            throw new ConnectException(ConnectCode.InvalidArgument, "deflate: truncated stream");

        var span = source.Span;
        // Check if data has ZLIB header (CMF byte with method=8 for deflate)
        bool hasZlibHeader = (span[0] & 0x0F) == 8 && ((span[0] * 256 + span[1]) % 31 == 0);

        ReadOnlyMemory<byte> deflateBody;
        if (hasZlibHeader)
        {
            // ZLIB framing (RFC 1950): 2-byte header + DEFLATE body + 4-byte Adler32 trailer.
            if (source.Length < 6)
                throw new ConnectException(ConnectCode.InvalidArgument, "deflate: truncated stream");
            deflateBody = source.Slice(2, source.Length - 6);
        }
        else
        {
            // Lenient fallback for peers sending raw DEFLATE without ZLIB framing. No
            // checksum is available on this path, so integrity cannot be verified.
            deflateBody = source;
        }

        using var input = GzipCompressor.AsStream(deflateBody);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        var scratch = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            uint adler = 1; // Adler32 seed
            int n;
            while ((n = deflate.Read(scratch, 0, scratch.Length)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"decompressed size exceeds limit {maxBytes}");
                adler = UpdateAdler32(adler, scratch.AsSpan(0, n));
                var dest = destination.GetSpan(n);
                scratch.AsSpan(0, n).CopyTo(dest);
                destination.Advance(n);
            }

            // DeflateStream reports EOF instead of failing when the input is cut off
            // mid-stream, so a truncated payload would otherwise pass through silently.
            // Verify the Adler32 trailer (big-endian, RFC 1950) against what we produced;
            // a truncated or corrupted stream cannot match a trailer at the expected offset.
            if (hasZlibHeader)
            {
                uint expected = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(span.Length - 4, 4));
                if (expected != adler)
                    throw new ConnectException(ConnectCode.InvalidArgument, "deflate: truncated or corrupted stream (Adler32 mismatch)");
            }
        }
        catch (InvalidDataException ex)
        {
            // Corrupt DEFLATE data. Connect maps decompression failures to
            // invalid_argument (connect-go: protocol errorf on decompress).
            throw new ConnectException(ConnectCode.InvalidArgument, $"deflate: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static uint Adler32(ReadOnlySpan<byte> data) => UpdateAdler32(1, data);

    /// <summary>Streaming Adler32 (RFC 1950). Seed with 1 and feed chunks in order.</summary>
    private static uint UpdateAdler32(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF, b = adler >> 16;
        const uint MOD = 65521;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }
}
