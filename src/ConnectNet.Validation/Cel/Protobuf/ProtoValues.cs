using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Wkt = Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Cel.Protobuf;

/// <summary>
/// Conversions between protobuf field values and CEL values in both directions, following the
/// specification's data conversion rules: numeric widening on read, range checks on write,
/// well-known type unwrapping, JSON (<c>Struct</c>/<c>Value</c>/<c>ListValue</c>) mapping,
/// and <c>Any</c> packing.
/// </summary>
internal static class ProtoValues
{
    private const long JsonSafeMax = (1L << 53) - 1;

    public static bool IsWrapper(MessageDescriptor type) => type.File.Name == "google/protobuf/wrappers.proto";

    // ---- protobuf -> CEL ----

    public static CelValue FromMessage(ProtoTypeProvider provider, IMessage message)
    {
        switch (message)
        {
            case Wkt.Timestamp ts:
                return new TimestampValue(ts.Seconds, ts.Nanos);
            case Wkt.Duration d:
            {
                if (!Numeric.TryMultiplyInt64(d.Seconds, DurationValue.NanosPerSecond, out var secs)
                    || !Numeric.TryAddInt64(secs, d.Nanos, out var total))
                    return ErrorValue.DurationOverflow;
                return new DurationValue(total);
            }
            case Wkt.Any any:
            {
                var unpacked = TryUnpack(provider, any);
                return unpacked == null
                    ? new ErrorValue("unknown type: '" + any.TypeUrl + "'")
                    : FromMessage(provider, unpacked);
            }
            case Wkt.Value v:
                return FromJsonValue(provider, v);
            case Wkt.Struct st:
                return FromStruct(provider, st);
            case Wkt.ListValue lv:
                return FromListValue(provider, lv);
            case Wkt.BoolValue b: return BoolValue.Of(b.Value);
            case Wkt.BytesValue by: return new BytesValue(by.Value.ToByteArray());
            case Wkt.DoubleValue dv: return DoubleValue.Of(dv.Value);
            case Wkt.FloatValue fv: return DoubleValue.Of(fv.Value);
            case Wkt.Int32Value i32: return IntValue.Of(i32.Value);
            case Wkt.Int64Value i64: return IntValue.Of(i64.Value);
            case Wkt.UInt32Value u32: return UintValue.Of(u32.Value);
            case Wkt.UInt64Value u64: return UintValue.Of(u64.Value);
            case Wkt.StringValue sv: return StringValue.Of(sv.Value);
            default:
                return new ProtoMessageValue(provider, message);
        }
    }

    public static IMessage? TryUnpack(ProtoTypeProvider provider, Wkt.Any any)
    {
        var typeName = Wkt.Any.GetTypeName(any.TypeUrl);
        var descriptor = provider.FindMessage(typeName);
        if (descriptor == null)
            return null;
        try
        {
            return descriptor.Parser.WithExtensionRegistry(provider.ExtensionRegistry).ParseFrom(any.Value);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    private static CelValue FromJsonValue(ProtoTypeProvider provider, Wkt.Value v)
    {
        switch (v.KindCase)
        {
            case Wkt.Value.KindOneofCase.BoolValue: return BoolValue.Of(v.BoolValue);
            case Wkt.Value.KindOneofCase.NumberValue: return DoubleValue.Of(v.NumberValue);
            case Wkt.Value.KindOneofCase.StringValue: return StringValue.Of(v.StringValue);
            case Wkt.Value.KindOneofCase.StructValue: return FromStruct(provider, v.StructValue);
            case Wkt.Value.KindOneofCase.ListValue: return FromListValue(provider, v.ListValue);
            default: return NullValue.Instance;
        }
    }

    private static CelValue FromStruct(ProtoTypeProvider provider, Wkt.Struct st)
    {
        var entries = new KeyValuePair<CelValue, CelValue>[st.Fields.Count];
        int i = 0;
        foreach (var kv in st.Fields)
            entries[i++] = new KeyValuePair<CelValue, CelValue>(StringValue.Of(kv.Key), FromJsonValue(provider, kv.Value));
        return MapValue.Create(entries);
    }

    private static CelValue FromListValue(ProtoTypeProvider provider, Wkt.ListValue lv)
    {
        var values = new CelValue[lv.Values.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = FromJsonValue(provider, lv.Values[i]);
        return new ListValue(values);
    }

    /// <summary>Whether a field is set, with the presence rules of the specification.</summary>
    public static bool IsSet(IMessage message, FieldDescriptor field)
    {
        var accessor = field.Accessor;
        if (field.IsMap)
            return ((IDictionary)accessor.GetValue(message)).Count > 0;
        if (field.IsRepeated)
            return ((IList)accessor.GetValue(message)).Count > 0;
        if (field.HasPresence)
            return accessor.HasValue(message);
        // proto3 implicit presence: set when not the default value.
        var value = accessor.GetValue(message);
        return value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            uint u => u != 0,
            ulong ul => ul != 0,
            float f => f != 0f,
            double d => d != 0d,
            string s => s.Length > 0,
            ByteString bs => !bs.IsEmpty,
            System.Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture) != 0,
            _ => true,
        };
    }

    /// <summary>The CEL value of a field, applying the default-value and wrapper rules for unset fields.</summary>
    public static CelValue FieldToCel(ProtoTypeProvider provider, IMessage message, FieldDescriptor field)
    {
        var raw = field.Accessor.GetValue(message);
        if (field.IsMap)
        {
            var dict = (IDictionary)raw;
            var keyField = field.MessageType.FindFieldByNumber(1);
            var valueField = field.MessageType.FindFieldByNumber(2);
            var entries = new KeyValuePair<CelValue, CelValue>[dict.Count];
            int i = 0;
            foreach (DictionaryEntry entry in dict)
            {
                var key = ScalarToCel(provider, keyField, entry.Key);
                var value = SingularToCel(provider, valueField, entry.Value);
                if (value.IsError) return value;
                entries[i++] = new KeyValuePair<CelValue, CelValue>(key, value);
            }
            return MapValue.Create(entries);
        }
        if (field.IsRepeated)
        {
            var list = (IList)raw;
            var values = new CelValue[list.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = SingularToCel(provider, field, list[i]);
                if (values[i].IsError) return values[i];
            }
            return new ListValue(values);
        }
        return SingularToCel(provider, field, raw);
    }

    private static CelValue SingularToCel(ProtoTypeProvider provider, FieldDescriptor field, object? raw)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
            case FieldType.Group:
                if (raw == null)
                {
                    // An unset wrapper reads as null; an unset message reads as its default instance.
                    if (IsWrapper(field.MessageType))
                        return NullValue.Instance;
                    return FromMessage(provider, field.MessageType.Parser.ParseFrom(Array.Empty<byte>()));
                }
                if (raw is IMessage msg)
                    return FromMessage(provider, msg);
                // Wrapper fields surface as nullable primitives.
                return ScalarToCel(provider, field.MessageType.FindFieldByNumber(1), raw);
            default:
                return ScalarToCel(provider, field, raw);
        }
    }

    private static CelValue ScalarToCel(ProtoTypeProvider provider, FieldDescriptor field, object? raw)
    {
        switch (raw)
        {
            case null: return NullValue.Instance;
            case bool b: return BoolValue.Of(b);
            case int i:
                return field.FieldType == FieldType.Enum ? EnumToCel(provider, field, i) : IntValue.Of(i);
            case long l: return IntValue.Of(l);
            case uint u: return UintValue.Of(u);
            case ulong ul: return UintValue.Of(ul);
            case float f: return DoubleValue.Of(f);
            case double d: return DoubleValue.Of(d);
            case string s: return StringValue.Of(s);
            case ByteString bs: return new BytesValue(bs.ToByteArray());
            case System.Enum e: return EnumToCel(provider, field, Convert.ToInt32(e, CultureInfo.InvariantCulture));
            case IMessage m: return FromMessage(provider, m);
            default: return new ErrorValue("unsupported field value of type " + raw.GetType().Name);
        }
    }

    private static CelValue EnumToCel(ProtoTypeProvider provider, FieldDescriptor field, int number) =>
        provider.EnumMode == EnumMode.Strong && field.EnumType != null
            ? new EnumValue(field.EnumType.FullName, number)
            : IntValue.Of(number);

    // ---- CEL -> protobuf ----

    /// <summary>Assigns a CEL value to a field; returns an error value instead of throwing.</summary>
    public static ErrorValue? SetField(ProtoTypeProvider provider, IMessage message, FieldDescriptor field, CelValue value)
    {
        var accessor = field.Accessor;
        if (field.IsMap)
        {
            if (value is not MapValue map)
                return TypeMismatch(field, value);
            var dict = (IDictionary)accessor.GetValue(message);
            var keyField = field.MessageType.FindFieldByNumber(1);
            var valueField = field.MessageType.FindFieldByNumber(2);
            foreach (var entry in map.Entries)
            {
                var key = ToClr(provider, keyField, entry.Key, out var error);
                if (error != null) return error;
                // Null values for message-typed entries are pruned, except for JSON and Any values.
                if (entry.Value is NullValue && PrunesNull(valueField))
                    continue;
                var val = ToClr(provider, valueField, entry.Value, out error);
                if (error != null) return error;
                dict[key!] = val;
            }
            return null;
        }
        if (field.IsRepeated)
        {
            if (value is not ListValue list)
                return TypeMismatch(field, value);
            var target = (IList)accessor.GetValue(message);
            foreach (var element in list.Elements)
            {
                if (element is NullValue && PrunesNull(field))
                    continue;
                var val = ToClr(provider, field, element, out var error);
                if (error != null) return error;
                target.Add(val);
            }
            return null;
        }
        if (value is NullValue && field.FieldType is FieldType.Message or FieldType.Group && PrunesNull(field))
        {
            // null leaves message, wrapper, timestamp and duration fields unset.
            return null;
        }
        var clr = ToClr(provider, field, value, out var err);
        if (err != null) return err;
        if (clr == null)
            return null;
        accessor.SetValue(message, clr);
        return null;
    }

    /// <summary>
    /// Whether a null assigned to the field is dropped (the field stays unset, the element or
    /// entry is omitted). Any and Value fields store the null; Struct and ListValue reject it.
    /// </summary>
    private static bool PrunesNull(FieldDescriptor field)
    {
        if (field.FieldType is not (FieldType.Message or FieldType.Group))
            return false;
        var name = field.MessageType.FullName;
        return name is not ("google.protobuf.Any" or "google.protobuf.Value" or "google.protobuf.Struct" or "google.protobuf.ListValue");
    }

    private static ErrorValue TypeMismatch(FieldDescriptor field, CelValue value) =>
        new("unsupported field type conversion: field '" + field.Name + "' of type '" + field.FieldType + "' from '" + value.TypeName + "'");

    private static ErrorValue RangeError(FieldDescriptor field, CelValue value) =>
        new("range error: value " + value + " does not fit field '" + field.Name + "' of type '" + field.FieldType + "'");

    /// <summary>Converts a singular CEL value to the CLR representation of a field.</summary>
    private static object? ToClr(ProtoTypeProvider provider, FieldDescriptor field, CelValue value, out ErrorValue? error)
    {
        error = null;
        switch (field.FieldType)
        {
            case FieldType.Bool:
                if (value is BoolValue b) return b.Value;
                break;
            case FieldType.String:
                if (value is StringValue s) return s.Value;
                break;
            case FieldType.Bytes:
                if (value is BytesValue by) return ByteString.CopyFrom(by.Value);
                break;
            case FieldType.Double:
                if (value is DoubleValue d) return d.Value;
                break;
            case FieldType.Float:
                if (value is DoubleValue f) return (float)f.Value;
                break;
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                if (value is IntValue i32)
                {
                    if (i32.Value < int.MinValue || i32.Value > int.MaxValue) { error = RangeError(field, value); return null; }
                    return (int)i32.Value;
                }
                break;
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                if (value is IntValue i64) return i64.Value;
                break;
            case FieldType.UInt32:
            case FieldType.Fixed32:
                if (value is UintValue u32)
                {
                    if (u32.Value > uint.MaxValue) { error = RangeError(field, value); return null; }
                    return (uint)u32.Value;
                }
                break;
            case FieldType.UInt64:
            case FieldType.Fixed64:
                if (value is UintValue u64) return u64.Value;
                break;
            case FieldType.Enum:
            {
                int number;
                if (value is IntValue ei)
                {
                    if (ei.Value < int.MinValue || ei.Value > int.MaxValue) { error = RangeError(field, value); return null; }
                    number = (int)ei.Value;
                }
                else if (value is EnumValue ev && ev.EnumTypeName == field.EnumType.FullName)
                {
                    number = ev.Number;
                }
                else
                {
                    break;
                }
                return field.EnumType.ClrType != null ? System.Enum.ToObject(field.EnumType.ClrType, number) : number;
            }
            case FieldType.Message:
            case FieldType.Group:
                return ToMessageField(provider, field, value, out error);
        }
        error = TypeMismatch(field, value);
        return null;
    }

    private static object? ToMessageField(ProtoTypeProvider provider, FieldDescriptor field, CelValue value, out ErrorValue? error)
    {
        error = null;
        var type = field.MessageType;
        if (IsWrapper(type))
        {
            // C# represents wrapper fields as nullable primitives; maps and lists hold the primitive.
            if (value is NullValue) return null;
            var inner = type.FindFieldByNumber(1);
            return ToClr(provider, inner, value, out error);
        }
        var message = ToMessage(provider, type, value, out error);
        return message;
    }

    /// <summary>Builds a message of the given type from a CEL value (identity for messages, packing for the well-known types).</summary>
    public static IMessage? ToMessage(ProtoTypeProvider provider, MessageDescriptor type, CelValue value, out ErrorValue? error)
    {
        error = null;
        switch (type.FullName)
        {
            case "google.protobuf.Any":
                return ToAny(provider, value, out error);
            case "google.protobuf.Value":
                return ToJsonValue(provider, value, out error);
            case "google.protobuf.Struct":
                return ToStruct(provider, value, out error);
            case "google.protobuf.ListValue":
                return ToListValue(provider, value, out error);
            case "google.protobuf.Timestamp":
                if (value is TimestampValue ts) return new Wkt.Timestamp { Seconds = ts.Seconds, Nanos = ts.Nanos };
                if (value is NullValue) return null;
                break;
            case "google.protobuf.Duration":
                if (value is DurationValue dur) return new Wkt.Duration { Seconds = dur.Seconds, Nanos = dur.Nanos };
                if (value is NullValue) return null;
                break;
            case "google.protobuf.BoolValue": if (value is BoolValue b) return new Wkt.BoolValue { Value = b.Value }; break;
            case "google.protobuf.BytesValue": if (value is BytesValue by) return new Wkt.BytesValue { Value = ByteString.CopyFrom(by.Value) }; break;
            case "google.protobuf.DoubleValue": if (value is DoubleValue d) return new Wkt.DoubleValue { Value = d.Value }; break;
            case "google.protobuf.FloatValue": if (value is DoubleValue f) return new Wkt.FloatValue { Value = (float)f.Value }; break;
            case "google.protobuf.Int32Value":
                if (value is IntValue i32)
                {
                    if (i32.Value < int.MinValue || i32.Value > int.MaxValue) { error = new ErrorValue("range error: " + value + " does not fit int32"); return null; }
                    return new Wkt.Int32Value { Value = (int)i32.Value };
                }
                break;
            case "google.protobuf.Int64Value": if (value is IntValue i64) return new Wkt.Int64Value { Value = i64.Value }; break;
            case "google.protobuf.UInt32Value":
                if (value is UintValue u32)
                {
                    if (u32.Value > uint.MaxValue) { error = new ErrorValue("range error: " + value + " does not fit uint32"); return null; }
                    return new Wkt.UInt32Value { Value = (uint)u32.Value };
                }
                break;
            case "google.protobuf.UInt64Value": if (value is UintValue u64) return new Wkt.UInt64Value { Value = u64.Value }; break;
            case "google.protobuf.StringValue": if (value is StringValue s) return new Wkt.StringValue { Value = s.Value }; break;
            default:
                if (value is ProtoMessageValue pm && pm.Descriptor == type)
                    return pm.Message;
                if (value is NullValue)
                    return null;
                break;
        }
        error = new ErrorValue("unsupported conversion from '" + value.TypeName + "' to " + type.FullName);
        return null;
    }

    private static Wkt.Any? ToAny(ProtoTypeProvider provider, CelValue value, out ErrorValue? error)
    {
        error = null;
        IMessage? inner = value switch
        {
            ProtoMessageValue pm => pm.Message,
            BoolValue b => new Wkt.BoolValue { Value = b.Value },
            BytesValue by => new Wkt.BytesValue { Value = ByteString.CopyFrom(by.Value) },
            DoubleValue d => new Wkt.DoubleValue { Value = d.Value },
            IntValue i => new Wkt.Int64Value { Value = i.Value },
            UintValue u => new Wkt.UInt64Value { Value = u.Value },
            StringValue s => new Wkt.StringValue { Value = s.Value },
            TimestampValue ts => new Wkt.Timestamp { Seconds = ts.Seconds, Nanos = ts.Nanos },
            DurationValue dur => new Wkt.Duration { Seconds = dur.Seconds, Nanos = dur.Nanos },
            NullValue => Wkt.Value.ForNull(),
            ListValue l => ToListValue(provider, l, out error),
            MapValue m => ToStruct(provider, m, out error),
            _ => null,
        };
        if (error != null)
            return null;
        if (inner == null)
        {
            error = new ErrorValue("unsupported conversion from '" + value.TypeName + "' to google.protobuf.Any");
            return null;
        }
        return inner is Wkt.Any any ? any : Wkt.Any.Pack(inner);
    }

    private static Wkt.Value? ToJsonValue(ProtoTypeProvider provider, CelValue value, out ErrorValue? error)
    {
        error = null;
        switch (value)
        {
            case NullValue: return Wkt.Value.ForNull();
            case BoolValue b: return Wkt.Value.ForBool(b.Value);
            case DoubleValue d: return Wkt.Value.ForNumber(d.Value);
            case IntValue i:
                return i.Value >= -JsonSafeMax && i.Value <= JsonSafeMax
                    ? Wkt.Value.ForNumber(i.Value)
                    : Wkt.Value.ForString(i.Value.ToString(CultureInfo.InvariantCulture));
            case UintValue u:
                return u.Value <= (ulong)JsonSafeMax
                    ? Wkt.Value.ForNumber(u.Value)
                    : Wkt.Value.ForString(u.Value.ToString(CultureInfo.InvariantCulture));
            case StringValue s: return Wkt.Value.ForString(s.Value);
            case BytesValue by: return Wkt.Value.ForString(Convert.ToBase64String(by.Value));
            case ListValue l:
            {
                var lv = ToListValue(provider, l, out error);
                return lv == null ? null : new Wkt.Value { ListValue = lv };
            }
            case MapValue m:
            {
                var st = ToStruct(provider, m, out error);
                return st == null ? null : Wkt.Value.ForStruct(st);
            }
            case TimestampValue ts: return Wkt.Value.ForString(GoFormat.FormatTimestamp(ts));
            case DurationValue dur: return Wkt.Value.ForString(GoFormat.FormatDuration(dur.Nanoseconds));
            case EnumValue e: return Wkt.Value.ForNumber(e.Number);
            case ProtoMessageValue pm:
            {
                // Through the canonical JSON mapping of the message.
                try
                {
                    var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithTypeRegistry(provider.TypeRegistry));
                    var json = formatter.Format(pm.Message);
                    return JsonParser.Default.Parse<Wkt.Value>(json);
                }
                catch (InvalidOperationException e)
                {
                    error = new ErrorValue("cannot convert " + pm.TypeName + " to JSON: " + e.Message);
                    return null;
                }
                catch (InvalidProtocolBufferException e)
                {
                    error = new ErrorValue("cannot convert " + pm.TypeName + " to JSON: " + e.Message);
                    return null;
                }
            }
            default:
                error = new ErrorValue("unsupported conversion from '" + value.TypeName + "' to google.protobuf.Value");
                return null;
        }
    }

    private static Wkt.Struct? ToStruct(ProtoTypeProvider provider, CelValue value, out ErrorValue? error)
    {
        error = null;
        if (value is not MapValue map)
        {
            error = new ErrorValue("unsupported conversion from '" + value.TypeName + "' to google.protobuf.Struct");
            return null;
        }
        var st = new Wkt.Struct();
        foreach (var entry in map.Entries)
        {
            if (entry.Key is not StringValue key)
            {
                error = new ErrorValue("unsupported map key type '" + entry.Key.TypeName + "' for google.protobuf.Struct");
                return null;
            }
            var v = ToJsonValue(provider, entry.Value, out error);
            if (error != null) return null;
            st.Fields[key.Value] = v;
        }
        return st;
    }

    private static Wkt.ListValue? ToListValue(ProtoTypeProvider provider, CelValue value, out ErrorValue? error)
    {
        error = null;
        if (value is not ListValue list)
        {
            error = new ErrorValue("unsupported conversion from '" + value.TypeName + "' to google.protobuf.ListValue");
            return null;
        }
        var lv = new Wkt.ListValue();
        foreach (var element in list.Elements)
        {
            var v = ToJsonValue(provider, element, out error);
            if (error != null) return null;
            lv.Values.Add(v);
        }
        return lv;
    }
}
