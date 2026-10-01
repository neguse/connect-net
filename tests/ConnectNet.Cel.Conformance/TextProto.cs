using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Cel.Conformance;

/// <summary>
/// A parser for the protobuf text format subset used by the cel-spec corpus: scalar and message
/// fields, repeated values (repeated fields and <c>[a, b]</c> lists), map entries, enums by name,
/// string escapes with adjacent-literal concatenation, <c>inf</c>/<c>nan</c>, comments, and
/// <c>[type.googleapis.com/full.Name] { ... }</c> expansions for <c>google.protobuf.Any</c>.
/// </summary>
public sealed class TextProtoParser
{
    private readonly string _text;
    private readonly TypeRegistry _registry;
    private int _pos;

    public TextProtoParser(string text, TypeRegistry registry)
    {
        _text = text;
        _registry = registry;
    }

    public static T Parse<T>(string text, TypeRegistry registry, IReadOnlyList<FileDescriptor>? files = null) where T : IMessage, new()
    {
        var parser = new TextProtoParser(text, registry) { Files = files ?? Array.Empty<FileDescriptor>() };
        var message = new T();
        parser.ParseFields(message, isTopLevel: true);
        return message;
    }

    private Exception Error(string message)
    {
        int line = 1, col = 1;
        for (int i = 0; i < _pos && i < _text.Length; i++)
        {
            if (_text[i] == '\n') { line++; col = 1; } else col++;
        }
        return new FormatException($"text proto parse error at {line}:{col}: {message}");
    }

    private void SkipWhitespace()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (c == '#')
            {
                while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private bool Peek(char c)
    {
        SkipWhitespace();
        return _pos < _text.Length && _text[_pos] == c;
    }

    private bool Accept(char c)
    {
        if (!Peek(c)) return false;
        _pos++;
        return true;
    }

    private void Expect(char c)
    {
        if (!Accept(c))
            throw Error($"expected '{c}'");
    }

    private string ReadIdentifier()
    {
        SkipWhitespace();
        int start = _pos;
        while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_' || _text[_pos] == '.' || _text[_pos] == '/'))
            _pos++;
        if (start == _pos)
            throw Error("expected identifier");
        return _text.Substring(start, _pos - start);
    }

    private void ParseFields(IMessage message, bool isTopLevel)
    {
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                if (!isTopLevel) throw Error("unexpected end of input");
                return;
            }
            if (!isTopLevel && (_text[_pos] == '}' || _text[_pos] == '>'))
                return;

            if (Accept('['))
            {
                var bracketed = ReadIdentifier();
                Expect(']');
                if (bracketed.Contains('/'))
                {
                    // Any expansion: [type.googleapis.com/pkg.Msg] { ... } (a colon may follow the bracket)
                    Accept(':');
                    var typeName = bracketed.Substring(bracketed.LastIndexOf('/') + 1);
                    var descriptor = _registry.Find(typeName) ?? throw Error("unknown type " + typeName);
                    var inner = descriptor.Parser.ParseFrom(Array.Empty<byte>());
                    ParseMessageBody(inner);
                    if (message is Any any)
                    {
                        any.TypeUrl = bracketed;
                        any.Value = inner.ToByteString();
                    }
                    else
                    {
                        throw Error("Any expansion on a non-Any message");
                    }
                }
                else
                {
                    // Extension field: [pkg.extension_name]: value
                    var extension = FindExtension(message.Descriptor, bracketed)
                        ?? throw Error($"unknown extension '{bracketed}' on {message.Descriptor.FullName}");
                    if (extension.FieldType != FieldType.Message || Peek(':'))
                        Expect(':');
                    ParseFieldValue(message, extension);
                }
                Accept(',');
                Accept(';');
                continue;
            }

            var name = ReadIdentifier();
            var field = message.Descriptor.FindFieldByName(name) ?? throw Error($"unknown field '{name}' on {message.Descriptor.FullName}");
            if (field.FieldType == FieldType.Message && !Peek(':'))
            {
                ParseFieldValue(message, field);
            }
            else
            {
                Expect(':');
                ParseFieldValue(message, field);
            }
            Accept(',');
            Accept(';');
        }
    }

    private readonly Dictionary<string, FieldDescriptor> _extensionsByName = new();

    private FieldDescriptor? FindExtension(MessageDescriptor extendee, string fullName)
    {
        if (_extensionsByName.Count == 0)
        {
            foreach (var file in AllFiles())
            {
                foreach (var ext in file.Extensions.UnorderedExtensions)
                    _extensionsByName[ext.FullName] = ext;
                foreach (var msg in file.MessageTypes)
                    CollectNestedExtensions(msg);
            }
        }
        return _extensionsByName.TryGetValue(fullName, out var found) && found.ExtendeeType == extendee ? found : null;
    }

    private void CollectNestedExtensions(MessageDescriptor msg)
    {
        foreach (var ext in msg.Extensions.UnorderedExtensions)
            _extensionsByName[ext.FullName] = ext;
        foreach (var nested in msg.NestedTypes)
            CollectNestedExtensions(nested);
    }

    private IEnumerable<FileDescriptor> AllFiles()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<FileDescriptor>(Files);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file.Name)) continue;
            yield return file;
            foreach (var dep in file.Dependencies) queue.Enqueue(dep);
        }
    }

    /// <summary>The files whose extensions may appear in the text; set by the caller.</summary>
    public IReadOnlyList<FileDescriptor> Files { get; set; } = Array.Empty<FileDescriptor>();

    private void ParseMessageBody(IMessage message)
    {
        SkipWhitespace();
        char open = _pos < _text.Length ? _text[_pos] : '\0';
        char close = open == '{' ? '}' : open == '<' ? '>' : throw Error("expected '{'");
        _pos++;
        ParseFields(message, isTopLevel: false);
        Expect(close);
    }

    private void ParseFieldValue(IMessage message, FieldDescriptor field)
    {
        if (field.IsMap)
        {
            var dict = (IDictionary)field.Accessor.GetValue(message);
            if (Accept('['))
            {
                while (!Peek(']'))
                {
                    AddMapEntry(dict, field);
                    if (!Accept(',')) break;
                }
                Expect(']');
            }
            else
            {
                AddMapEntry(dict, field);
            }
            return;
        }

        if (field.IsRepeated)
        {
            var list = (IList)field.Accessor.GetValue(message);
            if (Accept('['))
            {
                while (!Peek(']'))
                {
                    list.Add(ParseSingleValue(field));
                    if (!Accept(',')) break;
                }
                Expect(']');
            }
            else
            {
                list.Add(ParseSingleValue(field));
            }
            return;
        }

        field.Accessor.SetValue(message, ParseSingleValue(field));
    }

    private void AddMapEntry(IDictionary dict, FieldDescriptor field)
    {
        var entryType = field.MessageType;
        var keyField = entryType.FindFieldByNumber(1);
        var valueField = entryType.FindFieldByNumber(2);
        object? key = null;
        object? value = null;
        SkipWhitespace();
        char open = _text[_pos];
        char close = open == '{' ? '}' : '>';
        _pos++;
        while (!Peek(close))
        {
            var name = ReadIdentifier();
            if (name == "key")
            {
                Expect(':');
                key = ParseSingleValue(keyField);
            }
            else if (name == "value")
            {
                if (valueField.FieldType != FieldType.Message || Peek(':')) Expect(':');
                value = ParseSingleValue(valueField);
            }
            else
            {
                throw Error("unexpected map entry field " + name);
            }
            Accept(',');
            Accept(';');
        }
        Expect(close);
        if (key == null) throw Error("map entry without key");
        value ??= valueField.FieldType == FieldType.Message && !IsWrapper(valueField.MessageType)
            ? valueField.MessageType.Parser.ParseFrom(Array.Empty<byte>())
            : DefaultScalar(valueField);
        dict[key] = value;
    }

    private static bool IsWrapper(MessageDescriptor descriptor) =>
        descriptor.File.Name == "google/protobuf/wrappers.proto";

    private static object DefaultScalar(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.String => "",
        FieldType.Bool => false,
        FieldType.Bytes => ByteString.Empty,
        FieldType.Double => 0.0,
        FieldType.Float => 0f,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => 0,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 0L,
        FieldType.UInt32 or FieldType.Fixed32 => 0u,
        FieldType.UInt64 or FieldType.Fixed64 => 0ul,
        FieldType.Enum => 0,
        _ => throw new NotSupportedException(field.FieldType.ToString()),
    };

    private object ParseSingleValue(FieldDescriptor field)
    {
        SkipWhitespace();
        switch (field.FieldType)
        {
            case FieldType.Message:
            case FieldType.Group:
            {
                var inner = field.MessageType.Parser.ParseFrom(Array.Empty<byte>());
                ParseMessageBody(inner);
                // C# represents wrapper-typed fields as nullable primitives rather than messages.
                if (IsWrapper(field.MessageType))
                    return field.MessageType.FindFieldByNumber(1).Accessor.GetValue(inner);
                return inner;
            }
            case FieldType.String:
                return ReadString();
            case FieldType.Bytes:
                return ByteString.CopyFrom(ReadBytes());
            case FieldType.Bool:
            {
                var tok = ReadToken();
                return tok switch
                {
                    "true" or "True" or "t" or "1" => true,
                    "false" or "False" or "f" or "0" => false,
                    _ => throw Error("invalid bool " + tok),
                };
            }
            case FieldType.Enum:
            {
                var tok = ReadToken();
                var value = field.EnumType.FindValueByName(tok);
                if (value != null) return value.Number;
                if (int.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    return number;
                throw Error("invalid enum value " + tok);
            }
            case FieldType.Double:
                return ReadDouble();
            case FieldType.Float:
                return (float)ReadDouble();
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                return checked((int)ReadInteger());
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                return ReadInteger();
            case FieldType.UInt32:
            case FieldType.Fixed32:
                return checked((uint)ReadUnsigned());
            case FieldType.UInt64:
            case FieldType.Fixed64:
                return ReadUnsigned();
            default:
                throw Error("unsupported field type " + field.FieldType);
        }
    }

    private string ReadToken()
    {
        SkipWhitespace();
        int start = _pos;
        while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '-' or '+' or '.'))
            _pos++;
        if (start == _pos) throw Error("expected a value");
        return _text.Substring(start, _pos - start);
    }

    private double ReadDouble()
    {
        var tok = ReadToken();
        var t = tok.Length > 1 && (tok.EndsWith("f") || tok.EndsWith("F")) && char.IsAsciiDigit(tok[tok.Length - 2])
            ? tok.Substring(0, tok.Length - 1)
            : tok;
        switch (t.ToLowerInvariant())
        {
            case "inf": case "infinity": case "+inf": case "+infinity": return double.PositiveInfinity;
            case "-inf": case "-infinity": return double.NegativeInfinity;
            case "nan": case "-nan": return double.NaN;
        }
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            return ReadIntegerFromToken(t);
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        throw Error("invalid double " + tok);
    }

    private long ReadInteger()
    {
        return ReadIntegerFromToken(ReadToken());
    }

    private long ReadIntegerFromToken(string tok)
    {
        bool negative = tok.StartsWith("-", StringComparison.Ordinal);
        var t = negative ? tok.Substring(1) : tok.TrimStart('+');
        ulong magnitude;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            magnitude = ulong.Parse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (t.Length > 1 && t[0] == '0' && char.IsAsciiDigit(t[1]))
            magnitude = Convert.ToUInt64(t, 8);
        else if (!ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out magnitude))
            throw Error("invalid integer " + tok);
        if (negative)
        {
            if (magnitude > (ulong)long.MaxValue + 1) throw Error("integer out of range " + tok);
            return magnitude == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
        }
        if (magnitude > long.MaxValue) throw Error("integer out of range " + tok);
        return (long)magnitude;
    }

    private ulong ReadUnsigned()
    {
        var tok = ReadToken();
        if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(tok.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (ulong.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            return v;
        throw Error("invalid unsigned integer " + tok);
    }

    private string ReadString()
    {
        return Encoding.UTF8.GetString(ReadBytes());
    }

    /// <summary>Reads one or more adjacent quoted literals, decoding escapes to bytes.</summary>
    private byte[] ReadBytes()
    {
        var bytes = new List<byte>();
        bool any = false;
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _text.Length || (_text[_pos] != '"' && _text[_pos] != '\''))
                break;
            any = true;
            char quote = _text[_pos++];
            while (true)
            {
                if (_pos >= _text.Length) throw Error("unterminated string");
                char c = _text[_pos++];
                if (c == quote) break;
                if (c == '\n') throw Error("newline in string");
                if (c != '\\')
                {
                    AppendUtf8(bytes, c);
                    continue;
                }
                if (_pos >= _text.Length) throw Error("bad escape");
                char e = _text[_pos++];
                switch (e)
                {
                    case 'a': bytes.Add(7); break;
                    case 'b': bytes.Add(8); break;
                    case 'f': bytes.Add(12); break;
                    case 'n': bytes.Add(10); break;
                    case 'r': bytes.Add(13); break;
                    case 't': bytes.Add(9); break;
                    case 'v': bytes.Add(11); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    case '\'': bytes.Add((byte)'\''); break;
                    case '"': bytes.Add((byte)'"'); break;
                    case '?': bytes.Add((byte)'?'); break;
                    case 'x':
                    case 'X':
                    {
                        int value = 0, n = 0;
                        while (n < 2 && _pos < _text.Length && Uri.IsHexDigit(_text[_pos]))
                        {
                            value = value * 16 + Convert.ToInt32(_text[_pos].ToString(), 16);
                            _pos++;
                            n++;
                        }
                        if (n == 0) throw Error("bad hex escape");
                        bytes.Add((byte)value);
                        break;
                    }
                    case 'u':
                    case 'U':
                    {
                        int digits = e == 'u' ? 4 : 8;
                        int cp = int.Parse(_text.AsSpan(_pos, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        _pos += digits;
                        bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)));
                        break;
                    }
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int value = e - '0', n = 1;
                            while (n < 3 && _pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '7')
                            {
                                value = value * 8 + (_text[_pos] - '0');
                                _pos++;
                                n++;
                            }
                            bytes.Add((byte)value);
                            break;
                        }
                        throw Error("unknown escape \\" + e);
                }
            }
        }
        if (!any) throw Error("expected a string");
        return bytes.ToArray();
    }

    private void AppendUtf8(List<byte> bytes, char c)
    {
        if (char.IsHighSurrogate(c) && _pos < _text.Length && char.IsLowSurrogate(_text[_pos]))
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(new string(new[] { c, _text[_pos] })));
            _pos++;
        }
        else
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }
    }
}
