using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;

namespace ConnectNet;

/// <summary>
/// Implemented by messages that protoc-gen-connect-csharp generated proto-JSON code for, which
/// lets <see cref="JsonCodec"/> skip the UTF-16 round trip through Google.Protobuf.
/// </summary>
public interface IConnectJsonMessage
{
    void WriteJson(Utf8JsonWriter writer, JsonFormatter fallback);
}

/// <summary>Run-time support for generated proto-JSON code. Not intended for direct use.</summary>
public static class ConnectJson
{
    private static readonly JsonEncodedText NaN = JsonEncodedText.Encode("NaN");
    private static readonly JsonEncodedText PositiveInfinity = JsonEncodedText.Encode("Infinity");
    private static readonly JsonEncodedText NegativeInfinity = JsonEncodedText.Encode("-Infinity");

    public static void WriteMessage(Utf8JsonWriter writer, IMessage message, JsonFormatter fallback)
    {
        if (message is IConnectJsonMessage generated)
        {
            generated.WriteJson(writer, fallback);
        }
        else
        {
            writer.WriteRawValue(fallback.Format(message));
        }
    }

    public static void WriteInt64(Utf8JsonWriter writer, long value)
    {
        Span<byte> utf8 = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, utf8, out var written);
        writer.WriteStringValue(utf8.Slice(0, written));
    }

    public static void WriteUInt64(Utf8JsonWriter writer, ulong value)
    {
        Span<byte> utf8 = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, utf8, out var written);
        writer.WriteStringValue(utf8.Slice(0, written));
    }

    public static void WriteFloat(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value)) writer.WriteStringValue(NaN);
        else if (float.IsPositiveInfinity(value)) writer.WriteStringValue(PositiveInfinity);
        else if (float.IsNegativeInfinity(value)) writer.WriteStringValue(NegativeInfinity);
        else writer.WriteNumberValue(value);
    }

    public static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue(NaN);
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue(PositiveInfinity);
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue(NegativeInfinity);
        else writer.WriteNumberValue(value);
    }

    public static void WriteString(Utf8JsonWriter writer, string value)
    {
        try
        {
            writer.WriteStringValue(value);
        }
        catch (ArgumentException)
        {
            writer.WriteStringValue(ReplaceLoneSurrogates(value));
        }
    }

    public static void WriteBytes(Utf8JsonWriter writer, ByteString value)
        => writer.WriteBase64StringValue(value.Span);

    public static void WriteMapKey(Utf8JsonWriter writer, string key)
    {
        try
        {
            writer.WritePropertyName(key);
        }
        catch (ArgumentException)
        {
            writer.WritePropertyName(ReplaceLoneSurrogates(key));
        }
    }

    public static void WriteMapKey(Utf8JsonWriter writer, bool key) => writer.WritePropertyName(key ? "true" : "false");
    public static void WriteMapKey(Utf8JsonWriter writer, int key) => writer.WritePropertyName(key.ToString(CultureInfo.InvariantCulture));
    public static void WriteMapKey(Utf8JsonWriter writer, uint key) => writer.WritePropertyName(key.ToString(CultureInfo.InvariantCulture));
    public static void WriteMapKey(Utf8JsonWriter writer, long key) => writer.WritePropertyName(key.ToString(CultureInfo.InvariantCulture));
    public static void WriteMapKey(Utf8JsonWriter writer, ulong key) => writer.WritePropertyName(key.ToString(CultureInfo.InvariantCulture));

    private static string ReplaceLoneSurrogates(string value)
        => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value));
}
