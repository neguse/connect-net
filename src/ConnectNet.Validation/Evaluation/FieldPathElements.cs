using System;
using System.Globalization;
using System.Text;
using Buf.Validate;
using Google.Protobuf.Reflection;
using ProtoType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace ConnectNet.Validation.Evaluation;

/// <summary>
/// Builds <see cref="FieldPathElement"/>s the way the reference implementation does, and
/// renders paths in its textual form (<c>items[0].name</c>, <c>entries["key"]</c>).
/// </summary>
internal static class FieldPathElements
{
    public static ProtoType ToProtoType(FieldType type) => type switch
    {
        FieldType.Double => ProtoType.Double,
        FieldType.Float => ProtoType.Float,
        FieldType.Int64 => ProtoType.Int64,
        FieldType.UInt64 => ProtoType.Uint64,
        FieldType.Int32 => ProtoType.Int32,
        FieldType.Fixed64 => ProtoType.Fixed64,
        FieldType.Fixed32 => ProtoType.Fixed32,
        FieldType.Bool => ProtoType.Bool,
        FieldType.String => ProtoType.String,
        FieldType.Group => ProtoType.Group,
        FieldType.Message => ProtoType.Message,
        FieldType.Bytes => ProtoType.Bytes,
        FieldType.UInt32 => ProtoType.Uint32,
        FieldType.Enum => ProtoType.Enum,
        FieldType.SFixed32 => ProtoType.Sfixed32,
        FieldType.SFixed64 => ProtoType.Sfixed64,
        FieldType.SInt32 => ProtoType.Sint32,
        FieldType.SInt64 => ProtoType.Sint64,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown field type"),
    };

    /// <summary>The element naming a field: number, name (an extension by its bracketed full name) and type.</summary>
    public static FieldPathElement ForField(FieldDescriptor field) => new()
    {
        FieldNumber = field.FieldNumber,
        FieldName = field.IsExtension ? "[" + field.FullName + "]" : field.Name,
        FieldType = ToProtoType(field.FieldType),
    };

    /// <summary>The element of a repeated field's item.</summary>
    public static FieldPathElement ForItem(FieldPathElement field, int index) => new()
    {
        FieldNumber = field.FieldNumber,
        FieldName = field.FieldName,
        FieldType = field.FieldType,
        Index = (ulong)index,
    };

    /// <summary>The element of a map field's entry, carrying the key as its subscript.</summary>
    public static FieldPathElement ForEntry(FieldPathElement field, FieldDescriptor map, object key)
    {
        var keyField = map.MessageType.FindFieldByNumber(1);
        var valueField = map.MessageType.FindFieldByNumber(2);
        var element = new FieldPathElement
        {
            FieldNumber = field.FieldNumber,
            FieldName = field.FieldName,
            FieldType = field.FieldType,
            KeyType = ToProtoType(keyField.FieldType),
            ValueType = ToProtoType(valueField.FieldType),
        };
        switch (key)
        {
            case bool b: element.BoolKey = b; break;
            case int i: element.IntKey = i; break;
            case long l: element.IntKey = l; break;
            case uint u: element.UintKey = u; break;
            case ulong ul: element.UintKey = ul; break;
            case string s: element.StringKey = s; break;
            default: throw new InvalidOperationException("unexpected map key type " + keyField.FieldType);
        }
        return element;
    }

    /// <summary>The element naming a oneof, which has only a name.</summary>
    public static FieldPathElement ForOneof(OneofDescriptor oneof) => new() { FieldName = oneof.Name };

    /// <summary>Renders a path as the reference implementation's <c>FieldPathString</c> does.</summary>
    public static string Format(FieldPath? path)
    {
        if (path == null || path.Elements.Count == 0)
            return "";
        var sb = new StringBuilder();
        for (int i = 0; i < path.Elements.Count; i++)
        {
            var element = path.Elements[i];
            if (i > 0)
                sb.Append('.');
            sb.Append(element.FieldName);
            switch (element.SubscriptCase)
            {
                case FieldPathElement.SubscriptOneofCase.Index:
                    sb.Append('[').Append(element.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case FieldPathElement.SubscriptOneofCase.BoolKey:
                    sb.Append('[').Append(element.BoolKey ? "true" : "false").Append(']');
                    break;
                case FieldPathElement.SubscriptOneofCase.IntKey:
                    sb.Append('[').Append(element.IntKey.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case FieldPathElement.SubscriptOneofCase.UintKey:
                    sb.Append('[').Append(element.UintKey.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case FieldPathElement.SubscriptOneofCase.StringKey:
                    sb.Append('[').Append(GoQuote(element.StringKey)).Append(']');
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quotes a string the way Go's <c>strconv.Quote</c> does: double quotes, backslash
    /// escapes for the quote, the backslash and control characters, and printable text kept
    /// as is. Control characters in attacker-controlled keys therefore never reach a log line
    /// or terminal unescaped.
    /// </summary>
    public static string GoQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        var cp = char.ConvertToUtf32(c, s[i + 1]);
                        i++;
                        if (IsPrintable(cp))
                            sb.Append(char.ConvertFromUtf32(cp));
                        else
                            sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append("\\ufffd");
                    }
                    else if (c < 0x20 || c == 0x7F)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else if (IsPrintable(c))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsPrintable(int cp)
    {
        if (cp < 0x20 || cp == 0x7F) return false;
        if (cp < 0x7F) return true;
        if (cp >= 0x80 && cp < 0xA0) return false;
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        return category switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
            UnicodeCategory.SpaceSeparator => cp == 0x20,
            _ => true,
        };
    }
}
