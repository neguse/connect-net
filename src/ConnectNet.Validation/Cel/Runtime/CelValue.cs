using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ConnectNet.Validation.Cel.Checker;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>
/// A runtime CEL value. Values are immutable; errors are values too, so that the logical
/// operators and comprehensions can absorb them as the specification requires.
/// </summary>
internal abstract class CelValue
{
    /// <summary>The runtime type name, as returned by <c>type(x)</c>.</summary>
    public abstract string TypeName { get; }

    public virtual bool IsError => false;

    /// <summary>CEL equality: heterogeneous numeric equality, NaN never equal, structural for aggregates.</summary>
    public abstract bool EqualsValue(CelValue other);

    public override string ToString() => TypeName;
}

internal sealed class BoolValue : CelValue
{
    public static readonly BoolValue True = new(true);
    public static readonly BoolValue False = new(false);

    private BoolValue(bool value)
    {
        Value = value;
    }

    public bool Value { get; }

    public static BoolValue Of(bool value) => value ? True : False;

    public override string TypeName => "bool";

    public override bool EqualsValue(CelValue other) => other is BoolValue b && b.Value == Value;

    public override string ToString() => Value ? "true" : "false";
}

internal sealed class IntValue : CelValue
{
    private static readonly IntValue[] Small = CreateSmall();

    private static IntValue[] CreateSmall()
    {
        var arr = new IntValue[256];
        for (int i = 0; i < arr.Length; i++) arr[i] = new IntValue(i - 128);
        return arr;
    }

    private IntValue(long value)
    {
        Value = value;
    }

    public long Value { get; }

    public static IntValue Of(long value) =>
        value >= -128 && value < 128 ? Small[value + 128] : new IntValue(value);

    public override string TypeName => "int";

    public override bool EqualsValue(CelValue other) => other switch
    {
        IntValue i => i.Value == Value,
        UintValue u => Numeric.CompareIntUint(Value, u.Value) == 0,
        DoubleValue d => !double.IsNaN(d.Value) && Numeric.CompareIntDouble(Value, d.Value) == 0,
        _ => false,
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class UintValue : CelValue
{
    public UintValue(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public static UintValue Of(ulong value) => new(value);

    public override string TypeName => "uint";

    public override bool EqualsValue(CelValue other) => other switch
    {
        UintValue u => u.Value == Value,
        IntValue i => Numeric.CompareIntUint(i.Value, Value) == 0,
        DoubleValue d => !double.IsNaN(d.Value) && Numeric.CompareUintDouble(Value, d.Value) == 0,
        _ => false,
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + "u";
}

internal sealed class DoubleValue : CelValue
{
    public DoubleValue(double value)
    {
        Value = value;
    }

    public double Value { get; }

    public static DoubleValue Of(double value) => new(value);

    public override string TypeName => "double";

    public override bool EqualsValue(CelValue other)
    {
        if (double.IsNaN(Value)) return false;
        return other switch
        {
            DoubleValue d => Value == d.Value,
            IntValue i => Numeric.CompareIntDouble(i.Value, Value) == 0,
            UintValue u => Numeric.CompareUintDouble(u.Value, Value) == 0,
            _ => false,
        };
    }

    public override string ToString() => GoFormat.FormatDoubleG(Value);
}

internal sealed class StringValue : CelValue
{
    public static readonly StringValue Empty = new("");

    public StringValue(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static StringValue Of(string value) => value.Length == 0 ? Empty : new StringValue(value);

    public override string TypeName => "string";

    public override bool EqualsValue(CelValue other) => other is StringValue s && string.Equals(s.Value, Value, StringComparison.Ordinal);

    /// <summary>Number of Unicode code points.</summary>
    public int CodePointLength
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                if (char.IsHighSurrogate(Value[i]) && i + 1 < Value.Length && char.IsLowSurrogate(Value[i + 1])) i++;
                n++;
            }
            return n;
        }
    }

    public override string ToString() => "\"" + Value + "\"";
}

internal sealed class BytesValue : CelValue
{
    public static readonly BytesValue Empty = new(Array.Empty<byte>());

    public BytesValue(byte[] value)
    {
        Value = value;
    }

    /// <summary>The bytes; callers must not mutate the array.</summary>
    public byte[] Value { get; }

    public override string TypeName => "bytes";

    public override bool EqualsValue(CelValue other) => other is BytesValue b && b.Value.AsSpan().SequenceEqual(Value);

    public override string ToString() => "b\"" + Encoding.UTF8.GetString(Value) + "\"";
}

internal sealed class NullValue : CelValue
{
    public static readonly NullValue Instance = new();

    private NullValue()
    {
    }

    public override string TypeName => "null_type";

    public override bool EqualsValue(CelValue other) => other is NullValue;

    public override string ToString() => "null";
}

/// <summary>A type as a value: the result of <c>type(x)</c> or of a type identifier such as <c>int</c>.</summary>
internal sealed class TypeValue : CelValue
{
    public static readonly TypeValue BoolType = new("bool");
    public static readonly TypeValue BytesType = new("bytes");
    public static readonly TypeValue DoubleType = new("double");
    public static readonly TypeValue DurationType = new("google.protobuf.Duration");
    public static readonly TypeValue IntType = new("int");
    public static readonly TypeValue ListType = new("list");
    public static readonly TypeValue MapType = new("map");
    public static readonly TypeValue NullType = new("null_type");
    public static readonly TypeValue StringType = new("string");
    public static readonly TypeValue TimestampType = new("google.protobuf.Timestamp");
    public static readonly TypeValue TypeType = new("type");
    public static readonly TypeValue UintType = new("uint");

    public TypeValue(string name)
    {
        Name = name;
    }

    /// <summary>The runtime type name denoted by this value.</summary>
    public string Name { get; }

    public override string TypeName => "type";

    public override bool EqualsValue(CelValue other) => other is TypeValue t && string.Equals(t.Name, Name, StringComparison.Ordinal);

    /// <summary>The type value for a checker type, by its runtime name.</summary>
    public static TypeValue Of(CelType type) => type.Kind switch
    {
        TypeKind.Bool => BoolType,
        TypeKind.Bytes => BytesType,
        TypeKind.Double => DoubleType,
        TypeKind.Duration => DurationType,
        TypeKind.Int => IntType,
        TypeKind.List => ListType,
        TypeKind.Map => MapType,
        TypeKind.Null => NullType,
        TypeKind.String => StringType,
        TypeKind.Timestamp => TimestampType,
        TypeKind.Type => TypeType,
        TypeKind.Uint => UintType,
        _ => new TypeValue(type.Name),
    };

    public override string ToString() => Name;
}

/// <summary>A <c>google.protobuf.Duration</c>: a signed count of nanoseconds within the int64 range.</summary>
internal sealed class DurationValue : CelValue
{
    public DurationValue(long nanoseconds)
    {
        Nanoseconds = nanoseconds;
    }

    public long Nanoseconds { get; }

    public const long NanosPerSecond = 1_000_000_000;

    public long Seconds => Nanoseconds / NanosPerSecond;

    public int Nanos => (int)(Nanoseconds % NanosPerSecond);

    public override string TypeName => "google.protobuf.Duration";

    public override bool EqualsValue(CelValue other) => other is DurationValue d && d.Nanoseconds == Nanoseconds;

    public override string ToString() => "duration(\"" + GoFormat.FormatDuration(Nanoseconds) + "\")";
}

/// <summary>A <c>google.protobuf.Timestamp</c>: seconds since the Unix epoch plus nanoseconds, in UTC.</summary>
internal sealed class TimestampValue : CelValue
{
    /// <summary>0001-01-01T00:00:00Z.</summary>
    public const long MinUnixSeconds = -62135596800L;

    /// <summary>9999-12-31T23:59:59Z.</summary>
    public const long MaxUnixSeconds = 253402300799L;

    public TimestampValue(long seconds, int nanos)
    {
        Seconds = seconds;
        Nanos = nanos;
    }

    public long Seconds { get; }

    /// <summary>Nanoseconds within the second, 0 to 999,999,999.</summary>
    public int Nanos { get; }

    public static bool IsInRange(long seconds) => seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds;

    public override string TypeName => "google.protobuf.Timestamp";

    public override bool EqualsValue(CelValue other) => other is TimestampValue t && t.Seconds == Seconds && t.Nanos == Nanos;

    public int CompareTo(TimestampValue other)
    {
        int c = Seconds.CompareTo(other.Seconds);
        return c != 0 ? c : Nanos.CompareTo(other.Nanos);
    }

    /// <summary>The calendar date-time in UTC with 100ns precision (sub-tick nanoseconds dropped).</summary>
    public DateTime ToDateTime() =>
        DateTime.UnixEpoch.AddTicks(Seconds * TimeSpan.TicksPerSecond + Nanos / 100);

    public override string ToString() => "timestamp(\"" + GoFormat.FormatTimestamp(this) + "\")";
}

/// <summary>An evaluation error. Carries the message and, when known, the id of the expression that produced it.</summary>
internal sealed class ErrorValue : CelValue
{
    public ErrorValue(string message, long exprId = 0)
    {
        Message = message;
        ExprId = exprId;
    }

    public string Message { get; }

    public long ExprId { get; }

    public override bool IsError => true;

    public override string TypeName => "error";

    public override bool EqualsValue(CelValue other) => false;

    public static ErrorValue NoSuchOverload(string function, IReadOnlyList<CelValue> args)
    {
        var sb = new StringBuilder("no such overload: ").Append(function).Append('(');
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(args[i].TypeName);
        }
        return new ErrorValue(sb.Append(')').ToString());
    }

    public static ErrorValue NoSuchOverload(string function, CelValue arg) =>
        new("no such overload: " + function + "(" + arg.TypeName + ")");

    public static ErrorValue NoSuchOverload(string function, CelValue a, CelValue b) =>
        new("no such overload: " + function + "(" + a.TypeName + ", " + b.TypeName + ")");

    public static readonly ErrorValue DivideByZero = new("divide by zero");
    public static readonly ErrorValue ModulusByZero = new("modulus by zero");
    public static readonly ErrorValue IntOverflow = new("integer overflow");
    public static readonly ErrorValue UintOverflow = new("unsigned integer overflow");
    public static readonly ErrorValue TimestampOverflow = new("timestamp overflow");
    public static readonly ErrorValue DurationOverflow = new("duration overflow");

    public ErrorValue WithExprId(long id) => ExprId != 0 || id == 0 ? this : new ErrorValue(Message, id);

    public override string ToString() => "error: " + Message;
}

/// <summary>An immutable list value.</summary>
internal sealed class ListValue : CelValue
{
    public static readonly ListValue Empty = new(Array.Empty<CelValue>());

    public ListValue(IReadOnlyList<CelValue> elements)
    {
        Elements = elements;
    }

    public IReadOnlyList<CelValue> Elements { get; }

    public int Count => Elements.Count;

    public CelValue this[int index] => Elements[index];

    public override string TypeName => "list";

    public override bool EqualsValue(CelValue other)
    {
        if (other is not ListValue l || l.Count != Count) return false;
        for (int i = 0; i < Count; i++)
        {
            if (!Elements[i].EqualsValue(l.Elements[i])) return false;
        }
        return true;
    }

    public bool Contains(CelValue elem)
    {
        for (int i = 0; i < Count; i++)
        {
            if (elem.EqualsValue(Elements[i])) return true;
        }
        return false;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Elements[i]);
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>
/// The normalized form of a map key. CEL map keys are bool, int, uint or string; numeric keys
/// that denote the same number (<c>1</c>, <c>1u</c>, <c>1.0</c>) address the same entry.
/// </summary>
internal readonly struct MapKey : IEquatable<MapKey>
{
    private enum KeyKind : byte { Bool, Int, BigUint, String }

    private readonly KeyKind _kind;
    private readonly long _number;
    private readonly string? _string;

    private MapKey(KeyKind kind, long number, string? s)
    {
        _kind = kind;
        _number = number;
        _string = s;
    }

    /// <summary>
    /// Normalizes a value for lookup. Returns false for values that can never be a key, and
    /// for numbers without an exact integer representation.
    /// </summary>
    public static bool TryFromValue(CelValue value, out MapKey key)
    {
        switch (value)
        {
            case BoolValue b:
                key = new MapKey(KeyKind.Bool, b.Value ? 1 : 0, null);
                return true;
            case IntValue i:
                key = new MapKey(KeyKind.Int, i.Value, null);
                return true;
            case UintValue u:
                key = u.Value <= long.MaxValue
                    ? new MapKey(KeyKind.Int, (long)u.Value, null)
                    : new MapKey(KeyKind.BigUint, unchecked((long)u.Value), null);
                return true;
            case DoubleValue d:
                if (Numeric.TryDoubleToInt64Lossless(d.Value, out var l))
                {
                    key = new MapKey(KeyKind.Int, l, null);
                    return true;
                }
                if (Numeric.TryDoubleToUint64Lossless(d.Value, out var ul) && ul > long.MaxValue)
                {
                    key = new MapKey(KeyKind.BigUint, unchecked((long)ul), null);
                    return true;
                }
                key = default;
                return false;
            case StringValue s:
                key = new MapKey(KeyKind.String, 0, s.Value);
                return true;
            default:
                key = default;
                return false;
        }
    }

    /// <summary>Whether a value has a type that may be used as a map key at all.</summary>
    public static bool IsKeyType(CelValue value) => value is BoolValue or IntValue or UintValue or StringValue;

    public bool Equals(MapKey other) =>
        _kind == other._kind && _number == other._number && string.Equals(_string, other._string, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MapKey k && Equals(k);

    public override int GetHashCode() => HashCode.Combine(_kind, _number, _string);
}

/// <summary>An immutable map value with normalized key lookup and insertion-ordered entries.</summary>
internal sealed class MapValue : CelValue
{
    public static readonly MapValue Empty = new(Array.Empty<KeyValuePair<CelValue, CelValue>>(), new Dictionary<MapKey, int>());

    private readonly IReadOnlyList<KeyValuePair<CelValue, CelValue>> _entries;
    private readonly Dictionary<MapKey, int> _index;

    private MapValue(IReadOnlyList<KeyValuePair<CelValue, CelValue>> entries, Dictionary<MapKey, int> index)
    {
        _entries = entries;
        _index = index;
    }

    /// <summary>Builds a map; returns an error value for unsupported or repeated keys.</summary>
    public static CelValue Create(IReadOnlyList<KeyValuePair<CelValue, CelValue>> entries)
    {
        var index = new Dictionary<MapKey, int>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var key = entries[i].Key;
            if (!MapKey.IsKeyType(key))
                return new ErrorValue("unsupported map key type: '" + key.TypeName + "'");
            MapKey.TryFromValue(key, out var mk);
            if (!index.TryAdd(mk, i))
                return new ErrorValue("Failed with repeated key: " + key);
        }
        return new MapValue(entries, index);
    }

    public IReadOnlyList<KeyValuePair<CelValue, CelValue>> Entries => _entries;

    public int Count => _entries.Count;

    public override string TypeName => "map";

    public bool TryGet(CelValue key, out CelValue value)
    {
        if (MapKey.TryFromValue(key, out var mk) && _index.TryGetValue(mk, out var i))
        {
            value = _entries[i].Value;
            return true;
        }
        value = null!;
        return false;
    }

    public bool ContainsKey(CelValue key) => MapKey.TryFromValue(key, out var mk) && _index.ContainsKey(mk);

    public override bool EqualsValue(CelValue other)
    {
        if (other is not MapValue m || m.Count != Count) return false;
        foreach (var entry in _entries)
        {
            if (!m.TryGet(entry.Key, out var otherValue)) return false;
            if (!entry.Value.EqualsValue(otherValue)) return false;
        }
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_entries[i].Key).Append(": ").Append(_entries[i].Value);
        }
        return sb.Append('}').ToString();
    }
}

/// <summary>
/// A protobuf message value. The Protobuf adaptation layer provides the implementation; the
/// evaluator only needs field access, presence tests and equality.
/// </summary>
internal abstract class MessageValue : CelValue
{
    /// <summary>The field's CEL value, or an error for an undefined field.</summary>
    public abstract CelValue GetField(string name);

    /// <summary>Whether the field is set, or an error for an undefined field.</summary>
    public abstract CelValue HasField(string name);
}

/// <summary>A strongly typed protobuf enum value (only with the strong enum mode).</summary>
internal sealed class EnumValue : CelValue
{
    public EnumValue(string enumTypeName, int number)
    {
        EnumTypeName = enumTypeName;
        Number = number;
    }

    public string EnumTypeName { get; }

    public int Number { get; }

    public override string TypeName => EnumTypeName;

    public override bool EqualsValue(CelValue other) =>
        other is EnumValue e && e.Number == Number && string.Equals(e.EnumTypeName, EnumTypeName, StringComparison.Ordinal);

    public override string ToString() => EnumTypeName + "(" + Number.ToString(CultureInfo.InvariantCulture) + ")";
}

/// <summary>Numeric helpers shared by comparison, equality and conversion.</summary>
internal static class Numeric
{
    private const double TwoTo64 = 18446744073709551616.0;

    public static int CompareIntUint(long i, ulong u)
    {
        if (i < 0 || u > long.MaxValue) return -1;
        long cmp = i - (long)u;
        return cmp < 0 ? -1 : cmp > 0 ? 1 : 0;
    }

    public static int CompareDoubleInt(double d, long i)
    {
        if (d < long.MinValue) return -1;
        if (d > long.MaxValue) return 1;
        return CompareDouble(d, i);
    }

    public static int CompareIntDouble(long i, double d) => -CompareDoubleInt(d, i);

    public static int CompareDoubleUint(double d, ulong u)
    {
        if (d < 0) return -1;
        if (d > ulong.MaxValue) return 1;
        return CompareDouble(d, u);
    }

    public static int CompareUintDouble(ulong u, double d) => -CompareDoubleUint(d, u);

    public static int CompareDouble(double a, double b) => a < b ? -1 : a > b ? 1 : 0;

    public static bool TryDoubleToInt64Checked(double v, out long result)
    {
        result = 0;
        if (double.IsInfinity(v) || double.IsNaN(v) || v <= long.MinValue || v >= long.MaxValue)
            return false;
        result = (long)v;
        return true;
    }

    public static bool TryDoubleToUint64Checked(double v, out ulong result)
    {
        result = 0;
        if (double.IsInfinity(v) || double.IsNaN(v) || v < 0 || v >= TwoTo64)
            return false;
        result = (ulong)v;
        return true;
    }

    public static bool TryDoubleToInt64Lossless(double v, out long result) =>
        TryDoubleToInt64Checked(v, out result) && result == v;

    public static bool TryDoubleToUint64Lossless(double v, out ulong result) =>
        TryDoubleToUint64Checked(v, out result) && result == v;

    public static bool TryAddInt64(long x, long y, out long r)
    {
        r = 0;
        if ((y > 0 && x > long.MaxValue - y) || (y < 0 && x < long.MinValue - y)) return false;
        r = x + y;
        return true;
    }

    public static bool TrySubtractInt64(long x, long y, out long r)
    {
        r = 0;
        if ((y < 0 && x > long.MaxValue + y) || (y > 0 && x < long.MinValue + y)) return false;
        r = x - y;
        return true;
    }

    public static bool TryMultiplyInt64(long x, long y, out long r)
    {
        r = 0;
        if ((x == -1 && y == long.MinValue) || (y == -1 && x == long.MinValue)
            || (x > 0 && y > 0 && x > long.MaxValue / y)
            || (x > 0 && y < 0 && y < long.MinValue / x)
            || (x < 0 && y > 0 && x < long.MinValue / y)
            || (x < 0 && y < 0 && y < long.MaxValue / x))
            return false;
        r = x * y;
        return true;
    }

    public static bool TryNegateInt64(long x, out long r)
    {
        r = 0;
        if (x == long.MinValue) return false;
        r = -x;
        return true;
    }
}
