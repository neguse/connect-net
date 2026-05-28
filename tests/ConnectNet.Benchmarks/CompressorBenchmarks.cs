using BenchmarkDotNet.Attributes;
using ConnectNet.Pooling;

namespace ConnectNet.Benchmarks;

[MemoryDiagnoser]
public class CompressorBenchmarks
{
    private readonly GzipCompressor _gzip = new();
    private readonly DeflateCompressor _deflate = new();

    private byte[] _raw = null!;
    private byte[] _gzipBytes = null!;
    private byte[] _deflateBytes = null!;

    [Params(1024, 16 * 1024, 256 * 1024)]
    public int PayloadBytes;

    [GlobalSetup]
    public void Setup()
    {
        // Repeating pattern compresses well; mimics protobuf string fields with repetition.
        _raw = new byte[PayloadBytes];
        for (int i = 0; i < PayloadBytes; i++) _raw[i] = (byte)(i % 64);
        _gzipBytes = _gzip.CompressToArray(_raw);
        _deflateBytes = _deflate.CompressToArray(_raw);
    }

    [Benchmark] public byte[] Gzip_Compress() => _gzip.CompressToArray(_raw);
    [Benchmark] public byte[] Gzip_Decompress() => _gzip.DecompressToArray(_gzipBytes, 4 * 1024 * 1024);

    [Benchmark] public byte[] Deflate_Compress() => _deflate.CompressToArray(_raw);
    [Benchmark] public byte[] Deflate_Decompress() => _deflate.DecompressToArray(_deflateBytes, 4 * 1024 * 1024);
}
