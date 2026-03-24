using ConnectNet;
using ConnectNet.Tests.Proto;
using Xunit;

namespace ConnectNet.Tests;

public class ProtobufCodecTests
{
    private readonly ProtobufCodec _codec = new();

    [Fact]
    public void Name_ReturnsProto()
    {
        Assert.Equal("proto", _codec.Name);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var request = new HelloRequest { Name = "test" };
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("test", deserialized.Name);
    }

    [Fact]
    public void Serialize_EmptyMessage_ReturnsEmptyBytes()
    {
        var request = new HelloRequest();
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("", deserialized.Name);
    }

    [Fact]
    public void Deserialize_InvalidBytes_Throws()
    {
        var badBytes = new byte[] { 0xFF, 0xFF, 0xFF };
        Assert.Throws<Google.Protobuf.InvalidProtocolBufferException>(
            () => _codec.Deserialize<HelloRequest>(badBytes));
    }
}
