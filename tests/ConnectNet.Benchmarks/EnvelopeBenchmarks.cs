using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace ConnectNet.Benchmarks;

[MemoryDiagnoser]
public class EnvelopeBenchmarks
{
    private byte[] _payload = null!;
    private byte[] _envelopeBytes = null!;

    [Params(64, 4096, 65536)]
    public int PayloadBytes;

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new byte[PayloadBytes];
        for (int i = 0; i < PayloadBytes; i++) _payload[i] = (byte)i;

        using var ms = new MemoryStream();
        await Envelope.WriteAsync(ms, 0x00, _payload);
        _envelopeBytes = ms.ToArray();
    }

    [Benchmark]
    public async Task<byte[]?> Read()
    {
        using var ms = new MemoryStream(_envelopeBytes);
        var env = await Envelope.ReadAsync(ms);
        return env?.data;
    }

    [Benchmark]
    public async Task Write()
    {
        using var ms = new MemoryStream(capacity: PayloadBytes + 5);
        await Envelope.WriteAsync(ms, 0x00, _payload);
    }
}
