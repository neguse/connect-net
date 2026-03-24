using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet;

public class JsonCodec : ICodec
{
    private readonly JsonFormatter _formatter;
    private readonly JsonParser _parser;

    public string Name => "json";

    public JsonCodec()
    {
        _formatter = new JsonFormatter(JsonFormatter.Settings.Default);
        _parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    }

    public JsonCodec(TypeRegistry typeRegistry)
    {
        _formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithTypeRegistry(typeRegistry));
        _parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true).WithTypeRegistry(typeRegistry));
    }

    public byte[] Serialize(IMessage message)
    {
        var json = _formatter.Format(message);
        return Encoding.UTF8.GetBytes(json);
    }

    public T Deserialize<T>(byte[] data) where T : IMessage<T>, new()
    {
        var json = Encoding.UTF8.GetString(data);
        return _parser.Parse<T>(json);
    }

    public IMessage Deserialize(byte[] data, MessageParser parser)
    {
        var json = Encoding.UTF8.GetString(data);
        return parser.ParseJson(json);
    }
}
