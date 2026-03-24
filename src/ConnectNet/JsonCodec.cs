using System.Text;
using Google.Protobuf;

namespace ConnectNet;

public class JsonCodec : ICodec
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default);
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default);

    public string Name => "json";

    public byte[] Serialize(IMessage message)
    {
        var json = Formatter.Format(message);
        return Encoding.UTF8.GetBytes(json);
    }

    public T Deserialize<T>(byte[] data) where T : IMessage<T>, new()
    {
        var json = Encoding.UTF8.GetString(data);
        return Parser.Parse<T>(json);
    }

    public IMessage Deserialize(byte[] data, MessageParser parser)
    {
        var json = Encoding.UTF8.GetString(data);
        // Use the message descriptor to parse JSON into the correct message type
        var descriptor = parser.ParseFrom(System.Array.Empty<byte>()).Descriptor;
        return Parser.Parse(json, descriptor);
    }
}
