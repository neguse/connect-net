using System;
using System.Buffers;
using System.Text;
using ConnectNet.Pooling;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet;

public class JsonCodec : ICodec
{
    /// <summary>
    /// Recursion limit applied to JsonParser. Lower than the protobuf JsonParser default (100)
    /// to defend against pathologically deep JSON payloads in either request or response bodies.
    /// </summary>
    public const int DefaultRecursionLimit = 32;

    private readonly JsonFormatter _formatter;
    private readonly JsonParser _parser;

    public string Name => "json";

    public JsonCodec()
    {
        _formatter = new JsonFormatter(JsonFormatter.Settings.Default);
        _parser = new JsonParser(JsonParser.Settings.Default
            .WithIgnoreUnknownFields(true)
            .WithRecursionLimit(DefaultRecursionLimit));
    }

    public JsonCodec(TypeRegistry typeRegistry)
    {
        _formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithTypeRegistry(typeRegistry));
        _parser = new JsonParser(JsonParser.Settings.Default
            .WithIgnoreUnknownFields(true)
            .WithRecursionLimit(DefaultRecursionLimit)
            .WithTypeRegistry(typeRegistry));
    }

    public void Serialize(IMessage message, IBufferWriter<byte> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        // Google.Protobuf's JsonFormatter only produces strings; we must encode to UTF-8 once.
        // The intermediate string allocation is unavoidable until the upstream library exposes
        // a Utf8JsonWriter-compatible formatter.
        var json = _formatter.Format(message);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var dest = destination.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(json, dest);
        destination.Advance(byteCount);
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : IMessage<T>, new()
    {
        // JsonParser.Parse takes a string; the UTF-8 → UTF-16 conversion is unavoidable until
        // the upstream library exposes a span-based parser.
        var json = Encoding.UTF8.GetString(data.Span);
        return _parser.Parse<T>(json);
    }

    public IMessage Deserialize(ReadOnlyMemory<byte> data, MessageParser parser)
    {
        var json = Encoding.UTF8.GetString(data.Span);
        return parser.ParseJson(json);
    }
}
