using System;
using ConnectNet;
using Xunit;
using ConnectNet.Pooling;

namespace ConnectNet.Tests;

public class GzipCompressorTests
{
    private static byte[] MakePayload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31);
        return data;
    }

    [Fact]
    public void Name_ReturnsGzip()
    {
        var compressor = new GzipCompressor();
        Assert.Equal("gzip", compressor.Name);
    }

    [Fact]
    public void CompressDecompress_RoundTrips()
    {
        var compressor = new GzipCompressor();
        var original = System.Text.Encoding.UTF8.GetBytes("Hello, Gzip compression test!");
        var compressed = compressor.CompressToArray(original);
        var decompressed = compressor.DecompressToArray(compressed, 1024);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressDecompress_EmptyData()
    {
        var compressor = new GzipCompressor();
        var original = System.Array.Empty<byte>();
        var compressed = compressor.CompressToArray(original);
        var decompressed = compressor.DecompressToArray(compressed, 1024);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void Decompress_TruncatedStream_ThrowsInvalidArgument()
    {
        var compressor = new GzipCompressor();
        var compressed = compressor.CompressToArray(MakePayload(50000));
        var truncated = compressed.AsMemory(0, compressed.Length / 2);
        var ex = Assert.Throws<ConnectException>(() => compressor.DecompressToArray(truncated, 100_000));
        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Decompress_CorruptedTrailerCrc_ThrowsInvalidArgument()
    {
        var compressor = new GzipCompressor();
        var compressed = compressor.CompressToArray(MakePayload(1024));
        compressed[compressed.Length - 8] ^= 0xFF; // flip a byte of the CRC32 trailer
        var ex = Assert.Throws<ConnectException>(() => compressor.DecompressToArray(compressed, 100_000));
        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
    }
}
