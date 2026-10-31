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

    /// <summary>Reads the object at the reader's current token, leaving it on the closing brace.</summary>
    void MergeJson(ref Utf8JsonReader reader, JsonParser fallback);
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

    public static T ReadMessage<T>(ref Utf8JsonReader reader, JsonParser fallback) where T : IMessage<T>, new()
    {
        var message = new T();
        if (message is IConnectJsonMessage generated)
        {
            generated.MergeJson(ref reader, fallback);
            return message;
        }
        using var document = JsonDocument.ParseValue(ref reader);
        return (T)fallback.Parse(document.RootElement.GetRawText(), message.Descriptor);
    }

    // Generated readers run this once per candidate name, and unescaped text compares bytewise.
    public static bool TextEquals(ref Utf8JsonReader reader, JsonEncodedText expected)
        => reader.ValueIsEscaped
            ? reader.ValueTextEquals(expected.EncodedUtf8Bytes)
            : reader.ValueSpan.SequenceEqual(expected.EncodedUtf8Bytes);

    public static void ExpectToken(ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if (reader.TokenType != expected)
            throw new FormatException($"expected {expected} but found {reader.TokenType}");
    }

    public static void RejectNull(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
            throw new FormatException("repeated elements and map values cannot be null");
    }

    public static void MarkOneofSeen(Span<bool> seen, int slot)
    {
        if (seen[slot])
            throw new FormatException("a oneof was specified more than once");
        seen[slot] = true;
    }

    public static bool ReadBool(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            default: throw new FormatException($"expected a boolean but found {reader.TokenType}");
        }
    }

    public static bool ReadBoolKey(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("true")) return true;
        if (reader.ValueTextEquals("false")) return false;
        throw new FormatException("invalid boolean map key");
    }

    public static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String && reader.TokenType != JsonTokenType.PropertyName)
            throw new FormatException($"expected a string but found {reader.TokenType}");
        return reader.GetString()!;
    }

    public static ByteString ReadBytes(ref Utf8JsonReader reader)
    {
        // The decoded array is not shared with anything else, so it can be wrapped without a copy.
        if (reader.TokenType == JsonTokenType.String && reader.TryGetBytesFromBase64(out var decoded))
            return UnsafeByteOperations.UnsafeWrap(decoded);
        return ByteString.FromBase64(ReadString(ref reader));
    }

    public static int ReadInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) return value;
        return checked((int)ReadIntegral(ref reader, int.MinValue, int.MaxValue));
    }

    public static uint ReadUInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt32(out var value)) return value;
        return checked((uint)ReadIntegral(ref reader, uint.MinValue, uint.MaxValue));
    }

    public static long ReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value)) return value;
        if (IsPlainInteger(ref reader) && Utf8Parser.TryParse(reader.ValueSpan, out long parsed, out _)) return parsed;
        return checked((long)ReadIntegral(ref reader, long.MinValue, long.MaxValue));
    }

    public static ulong ReadUInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out var value)) return value;
        if (IsPlainInteger(ref reader) && Utf8Parser.TryParse(reader.ValueSpan, out ulong parsed, out _)) return parsed;
        return checked((ulong)ReadIntegral(ref reader, ulong.MinValue, ulong.MaxValue));
    }

    public static double ReadDouble(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return Finite(reader.GetDouble());

        var text = ReadString(ref reader);
        switch (text)
        {
            case "NaN": return double.NaN;
            case "Infinity": return double.PositiveInfinity;
            case "-Infinity": return double.NegativeInfinity;
        }
        ValidateNumericString(text);
        return Finite(double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture));
    }

    public static float ReadFloat(ref Utf8JsonReader reader)
    {
        var value = ReadDouble(ref reader);
        var single = (float)value;
        if (float.IsInfinity(single) && !double.IsInfinity(value))
            throw new FormatException("value out of range for a float");
        return single;
    }

    // Integers may be spelled 1.0 or 1e3, as numbers or as strings, provided they are whole.
    // 64-bit integers are JSON strings. The canonical form parses without building a string;
    // everything else (exponents, escapes, out of range) takes the general path.
    private static bool IsPlainInteger(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || reader.HasValueSequence) return false;
        var text = reader.ValueSpan;
        var start = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (start == text.Length || text[start] == '0') return false;
        for (var i = start; i < text.Length; i++)
        {
            if (!IsDigit((char)text[i])) return false;
        }
        return true;
    }

    private static decimal ReadIntegral(ref Utf8JsonReader reader, decimal min, decimal max)
    {
        decimal value;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetDecimal(out value))
                throw new FormatException("number out of range for an integer");
        }
        else
        {
            var text = ReadString(ref reader);
            ValidateNumericString(text);
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value))
                throw new FormatException($"invalid integer: {text}");
        }
        if (value != decimal.Truncate(value) || value < min || value > max)
            throw new FormatException($"value is not an integer in range: {value}");
        return value;
    }

    private static void ValidateNumericString(string text)
    {
        var start = text.StartsWith("-", StringComparison.Ordinal) ? 1 : 0;
        var valid = text.Length > start && (IsDigit(text[start]) || text[start] == '.');
        if (!valid || (text[start] == '0' && text.Length > start + 1 && IsDigit(text[start + 1])))
            throw new FormatException($"invalid number: {text}");
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static double Finite(double value)
        => double.IsInfinity(value) ? throw new FormatException("number out of range") : value;

    private static string ReplaceLoneSurrogates(string value)
        => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value));
}
