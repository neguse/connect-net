using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class DeflateCompressorTests
{
    [Fact]
    public void Name_ReturnsDeflate()
    {
        var compressor = new DeflateCompressor();
        Assert.Equal("deflate", compressor.Name);
    }

    [Fact]
    public void CompressDecompress_RoundTrips()
    {
        var compressor = new DeflateCompressor();
        var original = System.Text.Encoding.UTF8.GetBytes("Hello, Deflate compression test!");
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressDecompress_EmptyData()
    {
        var compressor = new DeflateCompressor();
        var original = System.Array.Empty<byte>();
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }
}
