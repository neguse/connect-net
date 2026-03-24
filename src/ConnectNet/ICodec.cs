using Google.Protobuf;

namespace ConnectNet;

public interface ICodec
{
    string Name { get; }
    byte[] Serialize(IMessage message);
    T Deserialize<T>(byte[] data) where T : IMessage<T>, new();
    IMessage Deserialize(byte[] data, MessageParser parser);
}
