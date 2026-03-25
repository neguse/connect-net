using System;
using System.IO;
using System.IO.Compression;

namespace ConnectNet;

public class DeflateCompressor : ICompressor
{
    public string Name => "deflate";

    public byte[] Compress(byte[] data)
    {
        // ZLIB format: 2-byte header + raw DEFLATE + 4-byte Adler32
        using var output = new MemoryStream();

        // ZLIB header: CMF=0x78 (deflate, window size 32K), FLG=0x01 (no dict, level fastest)
        // FLG must be set so that (CMF*256 + FLG) % 31 == 0
        // 0x78 * 256 + 0x01 = 30721, 30721 % 31 = 0 ✓
        output.WriteByte(0x78);
        output.WriteByte(0x01);

        // Raw DEFLATE data
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
            deflate.Flush();
        }

        // .NET's DeflateStream may produce 0 bytes for empty input.
        // A valid DEFLATE stream requires at least a final block, so add one if needed.
        if (data.Length == 0 && output.Length == 2)
        {
            // Write a final empty stored block: BFINAL=1, BTYPE=00 (stored), LEN=0, NLEN=0xFFFF
            output.WriteByte(0x01); // BFINAL=1, BTYPE=00
            output.WriteByte(0x00); // LEN low
            output.WriteByte(0x00); // LEN high
            output.WriteByte(0xFF); // NLEN low
            output.WriteByte(0xFF); // NLEN high
        }

        // Adler32 checksum (big-endian)
        uint checksum = Adler32(data);
        output.WriteByte((byte)(checksum >> 24));
        output.WriteByte((byte)(checksum >> 16));
        output.WriteByte((byte)(checksum >> 8));
        output.WriteByte((byte)(checksum));

        return output.ToArray();
    }

    public byte[] Decompress(byte[] data)
    {
        if (data.Length < 2)
            throw new InvalidDataException("Invalid ZLIB data");

        // Check if data has ZLIB header (CMF byte with method=8 for deflate)
        bool hasZlibHeader = (data[0] & 0x0F) == 8 && ((data[0] * 256 + data[1]) % 31 == 0);

        if (hasZlibHeader)
        {
            // Skip 2-byte ZLIB header, ignore trailing 4-byte Adler32
            using var input = new MemoryStream(data, 2, data.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        else
        {
            // Raw DEFLATE (no ZLIB wrapper)
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    private static uint Adler32(byte[] data)
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
