using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

    // Generated writers emit well-formed JSON by construction, and text that needs no escaping
    // for HTML stays as-is, matching JsonFormatter.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    [ThreadStatic] private static Utf8JsonWriter? t_writer;

    // Counts arrays as well as objects, so allow for each message level being wrapped in one.
    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = DefaultRecursionLimit * 2 };

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
        if (message is IConnectJsonMessage generated)
        {
            // Lease roughly what the JSON will need up front: bytes grow by a third in base64, and
            // starting small would make a pooled destination regrow and copy several times.
            var encodedSize = message.CalculateSize();
            destination.GetMemory(encodedSize + encodedSize / 2);
            var writer = t_writer ??= new Utf8JsonWriter(NoDestination.Instance, WriterOptions);
            writer.Reset(destination);
            generated.WriteJson(writer, _formatter);
            writer.Flush();
            // Drop the reference so a cached writer never keeps the destination alive.
            writer.Reset(NoDestination.Instance);
            return;
        }
        // Without generated code, JsonFormatter only produces strings; we must encode to UTF-8 once.
        var json = _formatter.Format(message);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var dest = destination.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(json, dest);
        destination.Advance(byteCount);
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : IMessage<T>, new()
    {
        var message = new T();
        if (message is IConnectJsonMessage generated)
        {
            MergeGenerated(generated, data.Span);
            return message;
        }
        // Without generated code, JsonParser.Parse takes a string, which costs a UTF-16 conversion.
        return _parser.Parse<T>(Encoding.UTF8.GetString(data.Span));
    }

    public IMessage Deserialize(ReadOnlyMemory<byte> data, MessageParser parser)
    {
        var message = parser.ParseFrom(Array.Empty<byte>());
        if (message is IConnectJsonMessage generated)
        {
            MergeGenerated(generated, data.Span);
            return message;
        }
        return parser.ParseJson(Encoding.UTF8.GetString(data.Span));
    }

    private void MergeGenerated(IConnectJsonMessage message, ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read())
                throw new FormatException("empty JSON document");
            message.MergeJson(ref reader, _parser);
            if (reader.Read())
                throw new FormatException("unexpected content after the JSON document");
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        // Unity WebGL strips System.Text.Json's messages, so building its exception for malformed
        // input fails with an ArgumentNullException from string.Format.
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    // Resetting a writer to a Stream allocates a buffer for it; a shared IBufferWriter does not.
    private sealed class NoDestination : IBufferWriter<byte>
    {
        public static readonly NoDestination Instance = new();
        public void Advance(int count) => throw new InvalidOperationException();
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException();
        public Span<byte> GetSpan(int sizeHint = 0) => throw new InvalidOperationException();
    }

    // Resetting a writer to a Stream allocates a buffer for it; a shared IBufferWriter does not.
    private sealed class NoDestination : IBufferWriter<byte>
    {
        public static readonly NoDestination Instance = new();
        public void Advance(int count) => throw new InvalidOperationException();
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException();
        public Span<byte> GetSpan(int sizeHint = 0) => throw new InvalidOperationException();
    }
}
