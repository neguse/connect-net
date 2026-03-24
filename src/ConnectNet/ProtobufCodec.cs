using Google.Protobuf;

namespace ConnectNet;

public class ProtobufCodec : ICodec
{
    public string Name => "proto";

    public byte[] Serialize(IMessage message)
    {
        return message.ToByteArray();
    }

    public T Deserialize<T>(byte[] data) where T : IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());
        return parser.ParseFrom(data);
    }

    public IMessage Deserialize(byte[] data, MessageParser parser)
    {
        return parser.ParseFrom(data);
    }
}
