using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Syntax;
using ConnectNet.Validation.Internal;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>A function implemented by a delegate over evaluated arguments.</summary>
internal sealed class DelegateFunction : ICelFunction
{
    private readonly Func<EvalContext, CelValue[], CelValue> _impl;

    public DelegateFunction(string name, Func<EvalContext, CelValue[], CelValue> impl)
    {
        Name = name;
        _impl = impl;
    }

    public string Name { get; }

    public CelValue Invoke(EvalContext ctx, CelValue[] args) => _impl(ctx, args);
}

/// <summary>The standard library at runtime. Dispatch is by the dynamic types of the arguments.</summary>
internal static class StandardFunctions
{
    public static FunctionRegistry AddTo(FunctionRegistry registry)
    {
        registry.Add(Fn(Operators.LogicalNot, LogicalNot));
        registry.Add(Fn(Operators.EqualsOp, (ctx, a) => (CelValue?)Arity(a, 2) ?? BoolValue.Of(a[0].EqualsValue(a[1]))));
        registry.Add(Fn(Operators.NotEquals, (ctx, a) => (CelValue?)Arity(a, 2) ?? BoolValue.Of(!a[0].EqualsValue(a[1]))));
        registry.Add(Fn(Operators.Add, Add));
        registry.Add(Fn(Operators.Subtract, Subtract));
        registry.Add(Fn(Operators.Multiply, Multiply));
        registry.Add(Fn(Operators.Divide, Divide));
        registry.Add(Fn(Operators.Modulo, Modulo));
        registry.Add(Fn(Operators.Negate, Negate));
        registry.Add(Fn(Operators.Less, (ctx, a) => Compare(Operators.Less, a, c => c < 0)));
        registry.Add(Fn(Operators.LessEquals, (ctx, a) => Compare(Operators.LessEquals, a, c => c <= 0)));
        registry.Add(Fn(Operators.Greater, (ctx, a) => Compare(Operators.Greater, a, c => c > 0)));
        registry.Add(Fn(Operators.GreaterEquals, (ctx, a) => Compare(Operators.GreaterEquals, a, c => c >= 0)));
        registry.Add(Fn(Operators.Index, Index));
        registry.Add(Fn(Operators.In, In));
        registry.Add(Fn(FunctionNames.Size, Size));
        registry.Add(Fn(FunctionNames.Contains, (ctx, a) => StringTest(FunctionNames.Contains, a, (s, t) => s.Contains(t, StringComparison.Ordinal))));
        registry.Add(Fn(FunctionNames.StartsWith, (ctx, a) => StringTest(FunctionNames.StartsWith, a, (s, t) => s.StartsWith(t, StringComparison.Ordinal))));
        registry.Add(Fn(FunctionNames.EndsWith, (ctx, a) => StringTest(FunctionNames.EndsWith, a, (s, t) => s.EndsWith(t, StringComparison.Ordinal))));
        registry.Add(Fn(FunctionNames.Matches, Matches));
        registry.Add(Fn(FunctionNames.Type, (ctx, a) => (CelValue?)Arity(a, 1) ?? TypeOf(a[0])));
        registry.Add(Fn(FunctionNames.Dyn, (ctx, a) => (CelValue?)Arity(a, 1) ?? a[0]));
        registry.Add(Fn(FunctionNames.Int, ToInt));
        registry.Add(Fn(FunctionNames.Uint, ToUint));
        registry.Add(Fn(FunctionNames.Double, ToDouble));
        registry.Add(Fn(FunctionNames.Bool, ToBool));
        registry.Add(Fn(FunctionNames.String, ToString));
        registry.Add(Fn(FunctionNames.Bytes, ToBytes));
        registry.Add(Fn(FunctionNames.Timestamp, ToTimestamp));
        registry.Add(Fn(FunctionNames.Duration, ToDuration));
        registry.Add(Fn(FunctionNames.GetFullYear, (ctx, a) => TimeAccessor(FunctionNames.GetFullYear, a, t => t.Year, null)));
        registry.Add(Fn(FunctionNames.GetMonth, (ctx, a) => TimeAccessor(FunctionNames.GetMonth, a, t => t.Month - 1, null)));
        registry.Add(Fn(FunctionNames.GetDayOfYear, (ctx, a) => TimeAccessor(FunctionNames.GetDayOfYear, a, t => t.DayOfYear - 1, null)));
        registry.Add(Fn(FunctionNames.GetDayOfMonth, (ctx, a) => TimeAccessor(FunctionNames.GetDayOfMonth, a, t => t.Day - 1, null)));
        registry.Add(Fn(FunctionNames.GetDate, (ctx, a) => TimeAccessor(FunctionNames.GetDate, a, t => t.Day, null)));
        registry.Add(Fn(FunctionNames.GetDayOfWeek, (ctx, a) => TimeAccessor(FunctionNames.GetDayOfWeek, a, t => (int)t.DayOfWeek, null)));
        registry.Add(Fn(FunctionNames.GetHours, (ctx, a) => TimeAccessor(FunctionNames.GetHours, a, t => t.Hour, d => d / 3_600_000_000_000L)));
        registry.Add(Fn(FunctionNames.GetMinutes, (ctx, a) => TimeAccessor(FunctionNames.GetMinutes, a, t => t.Minute, d => d / 60_000_000_000L)));
        registry.Add(Fn(FunctionNames.GetSeconds, (ctx, a) => TimeAccessor(FunctionNames.GetSeconds, a, t => t.Second, d => d / 1_000_000_000L)));
        registry.Add(Fn(FunctionNames.GetMilliseconds, (ctx, a) => TimeAccessor(FunctionNames.GetMilliseconds, a, t => (int)(t.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond), d => d % 1_000_000_000L / 1_000_000L)));
        return registry;
    }

    private static DelegateFunction Fn(string name, Func<EvalContext, CelValue[], CelValue> impl) => new(name, impl);

    private static ErrorValue? Arity(CelValue[] args, int n) =>
        args.Length == n ? null : new ErrorValue("no such overload: wrong number of arguments");

    private static CelValue LogicalNot(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(Operators.LogicalNot, a);
        return a[0] is BoolValue b ? BoolValue.Of(!b.Value) : ErrorValue.NoSuchOverload(Operators.LogicalNot, a[0]);
    }

    // ---- arithmetic ----

    private static CelValue Add(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Add, a);
        switch (a[0])
        {
            case IntValue x when a[1] is IntValue y:
                return Numeric.TryAddInt64(x.Value, y.Value, out var r) ? IntValue.Of(r) : ErrorValue.IntOverflow;
            case UintValue x when a[1] is UintValue y:
                return y.Value > 0 && x.Value > ulong.MaxValue - y.Value ? ErrorValue.UintOverflow : UintValue.Of(x.Value + y.Value);
            case DoubleValue x when a[1] is DoubleValue y:
                return DoubleValue.Of(x.Value + y.Value);
            case StringValue x when a[1] is StringValue y:
                ctx.Consume(x.Value.Length + y.Value.Length);
                return StringValue.Of(x.Value + y.Value);
            case BytesValue x when a[1] is BytesValue y:
            {
                ctx.Consume(x.Value.Length + y.Value.Length);
                var result = new byte[x.Value.Length + y.Value.Length];
                Buffer.BlockCopy(x.Value, 0, result, 0, x.Value.Length);
                Buffer.BlockCopy(y.Value, 0, result, x.Value.Length, y.Value.Length);
                return new BytesValue(result);
            }
            case ListValue x when a[1] is ListValue y:
            {
                ctx.Consume(x.Count + y.Count);
                if (x.Count == 0) return y;
                if (y.Count == 0) return x;
                var result = new CelValue[x.Count + y.Count];
                for (int i = 0; i < x.Count; i++) result[i] = x[i];
                for (int i = 0; i < y.Count; i++) result[x.Count + i] = y[i];
                return new ListValue(result);
            }
            case DurationValue x when a[1] is DurationValue y:
                return Numeric.TryAddInt64(x.Nanoseconds, y.Nanoseconds, out var sum) ? new DurationValue(sum) : ErrorValue.DurationOverflow;
            case TimestampValue t when a[1] is DurationValue dur:
                return AddTimestampDuration(t, dur.Nanoseconds);
            case DurationValue dur when a[1] is TimestampValue t:
                return AddTimestampDuration(t, dur.Nanoseconds);
            default:
                return ErrorValue.NoSuchOverload(Operators.Add, a);
        }
    }

    internal static CelValue AddTimestampDuration(TimestampValue t, long durationNanos)
    {
        long sec2 = durationNanos / DurationValue.NanosPerSecond;
        long nsec2 = durationNanos % DurationValue.NanosPerSecond;
        if (!Numeric.TryAddInt64(t.Seconds, sec2, out var sec)) return ErrorValue.TimestampOverflow;
        long nsec = t.Nanos + nsec2;
        if (nsec < 0 || nsec >= DurationValue.NanosPerSecond)
        {
            if (!Numeric.TryAddInt64(sec, nsec / DurationValue.NanosPerSecond, out sec)) return ErrorValue.TimestampOverflow;
            nsec -= nsec / DurationValue.NanosPerSecond * DurationValue.NanosPerSecond;
            if (nsec < 0)
            {
                if (!Numeric.TryAddInt64(sec, -1, out sec)) return ErrorValue.TimestampOverflow;
                nsec += DurationValue.NanosPerSecond;
            }
        }
        if (!TimestampValue.IsInRange(sec)) return ErrorValue.TimestampOverflow;
        return new TimestampValue(sec, (int)nsec);
    }

    private static CelValue Subtract(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Subtract, a);
        switch (a[0])
        {
            case IntValue x when a[1] is IntValue y:
                return Numeric.TrySubtractInt64(x.Value, y.Value, out var r) ? IntValue.Of(r) : ErrorValue.IntOverflow;
            case UintValue x when a[1] is UintValue y:
                return y.Value > x.Value ? ErrorValue.UintOverflow : UintValue.Of(x.Value - y.Value);
            case DoubleValue x when a[1] is DoubleValue y:
                return DoubleValue.Of(x.Value - y.Value);
            case DurationValue x when a[1] is DurationValue y:
                return Numeric.TrySubtractInt64(x.Nanoseconds, y.Nanoseconds, out var diff) ? new DurationValue(diff) : ErrorValue.DurationOverflow;
            case TimestampValue t when a[1] is DurationValue dur:
                return Numeric.TryNegateInt64(dur.Nanoseconds, out var neg) ? AddTimestampDuration(t, neg) : ErrorValue.DurationOverflow;
            case TimestampValue x when a[1] is TimestampValue y:
            {
                if (!Numeric.TrySubtractInt64(x.Seconds, y.Seconds, out var sec)) return ErrorValue.DurationOverflow;
                long nsec = x.Nanos - y.Nanos;
                if (!Numeric.TryMultiplyInt64(sec, DurationValue.NanosPerSecond, out var tsec)) return ErrorValue.DurationOverflow;
                if (!Numeric.TryAddInt64(tsec, nsec, out var total)) return ErrorValue.DurationOverflow;
                return new DurationValue(total);
            }
            default:
                return ErrorValue.NoSuchOverload(Operators.Subtract, a);
        }
    }

    private static CelValue Multiply(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Multiply, a);
        switch (a[0])
        {
            case IntValue x when a[1] is IntValue y:
                return Numeric.TryMultiplyInt64(x.Value, y.Value, out var r) ? IntValue.Of(r) : ErrorValue.IntOverflow;
            case UintValue x when a[1] is UintValue y:
                return y.Value != 0 && x.Value > ulong.MaxValue / y.Value ? ErrorValue.UintOverflow : UintValue.Of(x.Value * y.Value);
            case DoubleValue x when a[1] is DoubleValue y:
                return DoubleValue.Of(x.Value * y.Value);
            default:
                return ErrorValue.NoSuchOverload(Operators.Multiply, a);
        }
    }

    private static CelValue Divide(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Divide, a);
        switch (a[0])
        {
            case IntValue x when a[1] is IntValue y:
                if (y.Value == 0) return ErrorValue.DivideByZero;
                if (x.Value == long.MinValue && y.Value == -1) return ErrorValue.IntOverflow;
                return IntValue.Of(x.Value / y.Value);
            case UintValue x when a[1] is UintValue y:
                return y.Value == 0 ? ErrorValue.DivideByZero : UintValue.Of(x.Value / y.Value);
            case DoubleValue x when a[1] is DoubleValue y:
                return DoubleValue.Of(x.Value / y.Value);
            default:
                return ErrorValue.NoSuchOverload(Operators.Divide, a);
        }
    }

    private static CelValue Modulo(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Modulo, a);
        switch (a[0])
        {
            case IntValue x when a[1] is IntValue y:
                if (y.Value == 0) return ErrorValue.ModulusByZero;
                if (x.Value == long.MinValue && y.Value == -1) return ErrorValue.IntOverflow;
                return IntValue.Of(x.Value % y.Value);
            case UintValue x when a[1] is UintValue y:
                return y.Value == 0 ? ErrorValue.ModulusByZero : UintValue.Of(x.Value % y.Value);
            default:
                return ErrorValue.NoSuchOverload(Operators.Modulo, a);
        }
    }

    private static CelValue Negate(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(Operators.Negate, a);
        switch (a[0])
        {
            case IntValue x:
                return Numeric.TryNegateInt64(x.Value, out var r) ? IntValue.Of(r) : ErrorValue.IntOverflow;
            case DoubleValue x:
                return DoubleValue.Of(-x.Value);
            default:
                return ErrorValue.NoSuchOverload(Operators.Negate, a[0]);
        }
    }

    // ---- comparison ----

    /// <summary>Three-way comparison with the cross-type numeric rules; null when the pair is not orderable.</summary>
    internal static int? CompareValues(CelValue x, CelValue y, out ErrorValue? error)
    {
        error = null;
        switch (x)
        {
            case BoolValue bx when y is BoolValue by:
                return bx.Value.CompareTo(by.Value);
            case IntValue ix:
                switch (y)
                {
                    case IntValue iy: return ix.Value.CompareTo(iy.Value);
                    case UintValue uy: return Numeric.CompareIntUint(ix.Value, uy.Value);
                    case DoubleValue dy:
                        if (double.IsNaN(dy.Value)) { error = new ErrorValue("NaN values cannot be ordered"); return null; }
                        return Numeric.CompareIntDouble(ix.Value, dy.Value);
                }
                break;
            case UintValue ux:
                switch (y)
                {
                    case UintValue uy: return ux.Value.CompareTo(uy.Value);
                    case IntValue iy: return -Numeric.CompareIntUint(iy.Value, ux.Value);
                    case DoubleValue dy:
                        if (double.IsNaN(dy.Value)) { error = new ErrorValue("NaN values cannot be ordered"); return null; }
                        return Numeric.CompareUintDouble(ux.Value, dy.Value);
                }
                break;
            case DoubleValue dx:
                if (y is IntValue or UintValue or DoubleValue)
                {
                    if (double.IsNaN(dx.Value) || (y is DoubleValue dy0 && double.IsNaN(dy0.Value)))
                    {
                        error = new ErrorValue("NaN values cannot be ordered");
                        return null;
                    }
                    return y switch
                    {
                        DoubleValue dy => Numeric.CompareDouble(dx.Value, dy.Value),
                        IntValue iy => Numeric.CompareDoubleInt(dx.Value, iy.Value),
                        _ => Numeric.CompareDoubleUint(dx.Value, ((UintValue)y).Value),
                    };
                }
                break;
            case StringValue sx when y is StringValue sy:
                return Math.Sign(CompareCodePoints(sx.Value, sy.Value));
            case BytesValue bx when y is BytesValue by:
                return Math.Sign(bx.Value.AsSpan().SequenceCompareTo(by.Value));
            case TimestampValue tx when y is TimestampValue ty:
                return tx.CompareTo(ty);
            case DurationValue dx when y is DurationValue dy:
                return dx.Nanoseconds.CompareTo(dy.Nanoseconds);
        }
        return null;
    }

    /// <summary>Lexicographic comparison by Unicode code point, which equals comparison of the UTF-8 bytes.</summary>
    internal static int CompareCodePoints(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = CodePoints.Next(a, ref i);
            int cb = CodePoints.Next(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return 0;
    }

    private static CelValue Compare(string op, CelValue[] a, Func<int, bool> test)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(op, a);
        var cmp = CompareValues(a[0], a[1], out var error);
        if (error != null) return error;
        if (cmp == null) return ErrorValue.NoSuchOverload(op, a);
        return BoolValue.Of(test(cmp.Value));
    }

    // ---- aggregates ----

    private static CelValue Index(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.Index, a);
        switch (a[0])
        {
            case ListValue list:
            {
                long index;
                switch (a[1])
                {
                    case IntValue i:
                        index = i.Value;
                        break;
                    case DoubleValue d:
                        if (!Numeric.TryDoubleToInt64Lossless(d.Value, out index))
                            return new ErrorValue("unsupported index value " + d + " in list");
                        break;
                    case UintValue u:
                        if (u.Value > long.MaxValue)
                            return new ErrorValue("unsupported index value " + u + " in list");
                        index = (long)u.Value;
                        break;
                    default:
                        return new ErrorValue("unsupported index type '" + a[1].TypeName + "' in list");
                }
                if (index < 0 || index >= list.Count)
                    return new ErrorValue("index '" + index + "' out of range in list size '" + list.Count + "'");
                return list[(int)index];
            }
            case MapValue map:
                if (map.TryGet(a[1], out var value))
                    return value;
                return new ErrorValue("no such key: " + a[1]);
            case MessageValue message when a[1] is StringValue field:
                return message.GetField(field.Value);
            default:
                return ErrorValue.NoSuchOverload(Operators.Index, a);
        }
    }

    private static CelValue In(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2) return ErrorValue.NoSuchOverload(Operators.In, a);
        switch (a[1])
        {
            case ListValue list:
                ctx.Consume(list.Count);
                return BoolValue.Of(list.Contains(a[0]));
            case MapValue map:
                return BoolValue.Of(map.ContainsKey(a[0]));
            default:
                return ErrorValue.NoSuchOverload(Operators.In, a);
        }
    }

    private static CelValue Size(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Size, a);
        return a[0] switch
        {
            StringValue s => IntValue.Of(s.CodePointLength),
            BytesValue b => IntValue.Of(b.Value.Length),
            ListValue l => IntValue.Of(l.Count),
            MapValue m => IntValue.Of(m.Count),
            _ => ErrorValue.NoSuchOverload(FunctionNames.Size, a[0]),
        };
    }

    // ---- strings ----

    private static CelValue StringTest(string name, CelValue[] a, Func<string, string, bool> test)
    {
        if (a.Length == 2 && a[0] is StringValue s && a[1] is StringValue t)
            return BoolValue.Of(test(s.Value, t.Value));
        return ErrorValue.NoSuchOverload(name, a);
    }

    private static CelValue Matches(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2 || a[0] is not StringValue s || a[1] is not StringValue pattern)
            return ErrorValue.NoSuchOverload(FunctionNames.Matches, a);
        ctx.Consume(s.Value.Length + pattern.Value.Length);
        try
        {
            return BoolValue.Of(Re2Pattern.GetRegex(pattern.Value).IsMatch(s.Value));
        }
        catch (ArgumentException e)
        {
            return new ErrorValue("invalid regular expression: " + e.Message);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new CelEvaluationException("regular expression match timed out");
        }
    }

    // ---- types and conversions ----

    internal static TypeValue TypeOf(CelValue v) => v switch
    {
        BoolValue => TypeValue.BoolType,
        IntValue => TypeValue.IntType,
        UintValue => TypeValue.UintType,
        DoubleValue => TypeValue.DoubleType,
        StringValue => TypeValue.StringType,
        BytesValue => TypeValue.BytesType,
        NullValue => TypeValue.NullType,
        ListValue => TypeValue.ListType,
        MapValue => TypeValue.MapType,
        TypeValue => TypeValue.TypeType,
        TimestampValue => TypeValue.TimestampType,
        DurationValue => TypeValue.DurationType,
        _ => new TypeValue(v.TypeName),
    };

    private static CelValue ToInt(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Int, a);
        switch (a[0])
        {
            case IntValue i:
                return i;
            case UintValue u:
                return u.Value > long.MaxValue ? ErrorValue.IntOverflow : IntValue.Of((long)u.Value);
            case DoubleValue d:
                return Numeric.TryDoubleToInt64Checked(d.Value, out var r) ? IntValue.Of(r) : ErrorValue.IntOverflow;
            case StringValue s:
                return GoFormat.TryParseInt64(s.Value, out var parsed)
                    ? IntValue.Of(parsed)
                    : new ErrorValue("type conversion error from 'string' to 'int'");
            case TimestampValue t:
                return IntValue.Of(t.Seconds);
            case DurationValue dur:
                return IntValue.Of(dur.Nanoseconds);
            case EnumValue e:
                return IntValue.Of(e.Number);
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Int, a[0]);
        }
    }

    private static CelValue ToUint(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Uint, a);
        switch (a[0])
        {
            case UintValue u:
                return u;
            case IntValue i:
                return i.Value < 0 ? ErrorValue.UintOverflow : UintValue.Of((ulong)i.Value);
            case DoubleValue d:
                return Numeric.TryDoubleToUint64Checked(d.Value, out var r) ? UintValue.Of(r) : ErrorValue.UintOverflow;
            case StringValue s:
                return GoFormat.TryParseUint64(s.Value, out var parsed)
                    ? UintValue.Of(parsed)
                    : new ErrorValue("type conversion error from 'string' to 'uint'");
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Uint, a[0]);
        }
    }

    private static CelValue ToDouble(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Double, a);
        switch (a[0])
        {
            case DoubleValue d:
                return d;
            case IntValue i:
                return DoubleValue.Of(i.Value);
            case UintValue u:
                return DoubleValue.Of(u.Value);
            case StringValue s:
                return GoFormat.TryParseDouble(s.Value, out var parsed)
                    ? DoubleValue.Of(parsed)
                    : new ErrorValue("type conversion error from 'string' to 'double'");
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Double, a[0]);
        }
    }

    private static CelValue ToBool(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Bool, a);
        switch (a[0])
        {
            case BoolValue b:
                return b;
            case StringValue s:
                return GoFormat.TryParseBool(s.Value, out var parsed)
                    ? BoolValue.Of(parsed)
                    : new ErrorValue("type conversion error from 'string' to 'bool'");
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Bool, a[0]);
        }
    }

    /// <summary>The <c>string(x)</c> conversion; null when the value has no string conversion.</summary>
    internal static string? ConvertToString(CelValue v, out ErrorValue? error)
    {
        error = null;
        switch (v)
        {
            case StringValue s: return s.Value;
            case BoolValue b: return b.Value ? "true" : "false";
            case IntValue i: return i.Value.ToString(CultureInfo.InvariantCulture);
            case UintValue u: return u.Value.ToString(CultureInfo.InvariantCulture);
            case DoubleValue d: return GoFormat.FormatDoubleG(d.Value);
            case BytesValue b:
            {
                if (!Utf8.IsValid(b.Value))
                {
                    error = new ErrorValue("invalid UTF-8 in bytes, cannot convert to string");
                    return null;
                }
                return Encoding.UTF8.GetString(b.Value);
            }
            case TimestampValue t: return GoFormat.FormatTimestamp(t);
            case DurationValue d: return GoFormat.FormatDuration(d.Nanoseconds);
            case NullValue: return "null";
            case TypeValue t: return t.Name;
            default: return null;
        }
    }

    private static CelValue ToString(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.String, a);
        if (a[0] is NullValue or TypeValue) return ErrorValue.NoSuchOverload(FunctionNames.String, a[0]);
        var s = ConvertToString(a[0], out var error);
        if (error != null) return error;
        if (s == null) return ErrorValue.NoSuchOverload(FunctionNames.String, a[0]);
        ctx.Consume(s.Length);
        return StringValue.Of(s);
    }

    private static CelValue ToBytes(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Bytes, a);
        switch (a[0])
        {
            case BytesValue b:
                return b;
            case StringValue s:
                ctx.Consume(s.Value.Length);
                return new BytesValue(Encoding.UTF8.GetBytes(s.Value));
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Bytes, a[0]);
        }
    }

    private static CelValue ToTimestamp(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Timestamp, a);
        switch (a[0])
        {
            case TimestampValue t:
                return t;
            case IntValue i:
                return TimestampValue.IsInRange(i.Value) ? new TimestampValue(i.Value, 0) : ErrorValue.TimestampOverflow;
            case StringValue s:
                if (!GoFormat.TryParseTimestamp(s.Value, out var seconds, out var nanos))
                    return new ErrorValue("invalid RFC 3339 timestamp: " + s);
                return TimestampValue.IsInRange(seconds) ? new TimestampValue(seconds, nanos) : ErrorValue.TimestampOverflow;
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Timestamp, a[0]);
        }
    }

    private static CelValue ToDuration(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1) return ErrorValue.NoSuchOverload(FunctionNames.Duration, a);
        switch (a[0])
        {
            case DurationValue d:
                return d;
            case StringValue s:
                return GoFormat.TryParseDuration(s.Value, out var nanos)
                    ? new DurationValue(nanos)
                    : new ErrorValue("invalid duration: " + s);
            default:
                return ErrorValue.NoSuchOverload(FunctionNames.Duration, a[0]);
        }
    }

    // ---- time accessors ----

    private static CelValue TimeAccessor(string name, CelValue[] a, Func<DateTime, long> timestampPart,
        Func<long, long>? durationPart)
    {
        if (a.Length == 1 && a[0] is DurationValue d && durationPart != null)
            return IntValue.Of(durationPart(d.Nanoseconds));
        if (a.Length >= 1 && a[0] is TimestampValue t)
        {
            if (a.Length == 1)
                return IntValue.Of(timestampPart(t.ToDateTime()));
            if (a.Length == 2 && a[1] is StringValue tz)
            {
                var local = ToTimeZone(t, tz.Value, out var error);
                if (error != null) return error;
                return IntValue.Of(timestampPart(local));
            }
        }
        return ErrorValue.NoSuchOverload(name, a);
    }

    /// <summary>The wall-clock date-time of a timestamp in a named or fixed-offset time zone.</summary>
    internal static DateTime ToTimeZone(TimestampValue t, string tz, out ErrorValue? error)
    {
        error = null;
        var utc = t.ToDateTime();
        int colon = tz.IndexOf(':');
        if (colon < 0)
        {
            if (tz == "UTC")
                return utc;
            var zone = TimeZones.Find(tz);
            if (zone == null)
            {
                error = new ErrorValue("unknown time zone " + tz);
                return utc;
            }
            var offset = zone.GetUtcOffset(utc);
            return DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified);
        }

        if (!int.TryParse(tz.AsSpan(0, colon), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(tz.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            error = new ErrorValue("invalid time zone offset: " + tz);
            return utc;
        }
        if (minutes < 0 || minutes > 59)
        {
            error = new ErrorValue("timezone offset minutes out of range [0, 59]: " + tz);
            return utc;
        }
        int offsetMinutes = tz[0] == '-' ? hours * 60 - minutes : hours * 60 + minutes;
        return DateTime.SpecifyKind(utc.AddMinutes(offsetMinutes), DateTimeKind.Unspecified);
    }
}

internal static class Utf8
{
    private static readonly Encoding Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

    public static bool IsValid(byte[] bytes)
    {
        try
        {
            Strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

internal static class CodePoints
{
    /// <summary>Reads one code point at <paramref name="i"/>, advancing it; an unpaired surrogate is returned as itself.</summary>
    public static int Next(string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            int cp = char.ConvertToUtf32(c, s[i + 1]);
            i += 2;
            return cp;
        }
        i++;
        return c;
    }

    /// <summary>Splits a string into code points.</summary>
    public static int[] Of(string s)
    {
        var list = new List<int>(s.Length);
        int i = 0;
        while (i < s.Length) list.Add(Next(s, ref i));
        return list.ToArray();
    }

    public static string ToString(int[] cps, int start, int count)
    {
        var sb = new StringBuilder(count);
        for (int i = start; i < start + count; i++)
        {
            if (cps[i] < 0x10000) sb.Append((char)cps[i]);
            else sb.Append(char.ConvertFromUtf32(cps[i]));
        }
        return sb.ToString();
    }
}
