using BenchmarkDotNet.Attributes;
using ConnectNet.Pooling;
using ConnectNet.Tests.Proto;

namespace ConnectNet.Benchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly ProtobufCodec _proto = new();
    private readonly JsonCodec _json = new();

    private HelloRequest _msg = null!;
    private byte[] _protoBytes = null!;
    private byte[] _jsonBytes = null!;

    [Params(16, 256, 4096)]
    public int PayloadBytes;

    [GlobalSetup]
    public void Setup()
    {
        _msg = new HelloRequest { Name = new string('a', PayloadBytes) };
        _protoBytes = _proto.SerializeToArray(_msg);
        _jsonBytes = _json.SerializeToArray(_msg);
    }

    [Benchmark] public byte[] Proto_Serialize() => _proto.SerializeToArray(_msg);
    [Benchmark] public HelloRequest Proto_Deserialize() => _proto.Deserialize<HelloRequest>(_protoBytes);

    [Benchmark] public byte[] Json_Serialize() => _json.SerializeToArray(_msg);
    [Benchmark] public HelloRequest Json_Deserialize() => _json.Deserialize<HelloRequest>(_jsonBytes);
}
