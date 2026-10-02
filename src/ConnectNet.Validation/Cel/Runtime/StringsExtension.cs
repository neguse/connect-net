using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ConnectNet.Validation.Cel.Checker;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>The strings extension at runtime, with code point (not UTF-16) indexing.</summary>
internal static class StringsExtension
{
    public static FunctionRegistry AddTo(FunctionRegistry registry)
    {
        registry.Add(new DelegateFunction(FunctionNames.CharAt, CharAt));
        registry.Add(new DelegateFunction(FunctionNames.IndexOf, IndexOf));
        registry.Add(new DelegateFunction(FunctionNames.LastIndexOf, LastIndexOf));
        registry.Add(new DelegateFunction(FunctionNames.LowerAscii, (ctx, a) => AsciiCase(FunctionNames.LowerAscii, a, false)));
        registry.Add(new DelegateFunction(FunctionNames.UpperAscii, (ctx, a) => AsciiCase(FunctionNames.UpperAscii, a, true)));
        registry.Add(new DelegateFunction(FunctionNames.Replace, Replace));
        registry.Add(new DelegateFunction(FunctionNames.Split, Split));
        registry.Add(new DelegateFunction(FunctionNames.Substring, Substring));
        registry.Add(new DelegateFunction(FunctionNames.Trim, Trim));
        registry.Add(new DelegateFunction(FunctionNames.Format, Format));
        registry.Add(new DelegateFunction(FunctionNames.StringsQuote, Quote));
        registry.Add(new DelegateFunction(FunctionNames.Join, Join));
        registry.Add(new DelegateFunction(FunctionNames.Reverse, Reverse));
        return registry;
    }

    private static CelValue CharAt(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2 || a[0] is not StringValue s || a[1] is not IntValue i)
            return ErrorValue.NoSuchOverload(FunctionNames.CharAt, a);
        var cps = CodePoints.Of(s.Value);
        if (i.Value < 0 || i.Value > cps.Length)
            return new ErrorValue("index out of range: " + i.Value);
        if (i.Value == cps.Length)
            return StringValue.Empty;
        return StringValue.Of(CodePoints.ToString(cps, (int)i.Value, 1));
    }

    private static CelValue IndexOf(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 2 || a.Length > 3 || a[0] is not StringValue s || a[1] is not StringValue sub
            || (a.Length == 3 && a[2] is not IntValue))
            return ErrorValue.NoSuchOverload(FunctionNames.IndexOf, a);
        long offset = a.Length == 3 ? ((IntValue)a[2]).Value : 0;
        if (offset < 0)
            return new ErrorValue("index out of range: " + offset);
        var runes = CodePoints.Of(s.Value);
        ctx.Consume(runes.Length);
        if (offset > runes.Length)
            return new ErrorValue("index out of range: " + offset);
        if (sub.Value.Length == 0)
            return IntValue.Of(offset);
        var subrunes = CodePoints.Of(sub.Value);
        if (offset >= runes.Length)
            return IntValue.Of(-1);
        for (int i = (int)offset; i < runes.Length - (subrunes.Length - 1); i++)
        {
            if (MatchesAt(runes, subrunes, i))
                return IntValue.Of(i);
        }
        return IntValue.Of(-1);
    }

    private static CelValue LastIndexOf(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 2 || a.Length > 3 || a[0] is not StringValue s || a[1] is not StringValue sub
            || (a.Length == 3 && a[2] is not IntValue))
            return ErrorValue.NoSuchOverload(FunctionNames.LastIndexOf, a);
        var runes = CodePoints.Of(s.Value);
        ctx.Consume(runes.Length);
        long offset;
        if (a.Length == 3)
        {
            offset = ((IntValue)a[2]).Value;
        }
        else
        {
            if (sub.Value.Length == 0)
                return IntValue.Of(runes.Length);
            if (s.Value.Length < sub.Value.Length)
                return IntValue.Of(-1);
            offset = runes.Length - 1;
        }
        if (offset < 0 || offset > runes.Length)
            return new ErrorValue("index out of range: " + offset);
        if (sub.Value.Length == 0)
            return IntValue.Of(offset);
        var subrunes = CodePoints.Of(sub.Value);
        if (offset >= runes.Length)
            return IntValue.Of(-1);
        int off = (int)offset;
        if (off > runes.Length - subrunes.Length)
            off = runes.Length - subrunes.Length;
        for (int i = off; i >= 0; i--)
        {
            if (MatchesAt(runes, subrunes, i))
                return IntValue.Of(i);
        }
        return IntValue.Of(-1);
    }

    private static bool MatchesAt(int[] runes, int[] sub, int at)
    {
        if (at + sub.Length > runes.Length) return false;
        for (int j = 0; j < sub.Length; j++)
        {
            if (runes[at + j] != sub[j]) return false;
        }
        return true;
    }

    private static CelValue AsciiCase(string name, CelValue[] a, bool upper)
    {
        if (a.Length != 1 || a[0] is not StringValue s)
            return ErrorValue.NoSuchOverload(name, a);
        var chars = s.Value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c <= 0x7f)
                chars[i] = upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
        }
        return StringValue.Of(new string(chars));
    }

    private static CelValue Replace(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 3 || a.Length > 4 || a[0] is not StringValue s || a[1] is not StringValue oldS
            || a[2] is not StringValue newS || (a.Length == 4 && a[3] is not IntValue))
            return ErrorValue.NoSuchOverload(FunctionNames.Replace, a);
        long n = a.Length == 4 ? ((IntValue)a[3]).Value : -1;
        ctx.Consume(s.Value.Length);
        return StringValue.Of(GoReplace(s.Value, oldS.Value, newS.Value, n));
    }

    /// <summary>Go's <c>strings.Replace</c>: at most n replacements (all when negative); an empty old string matches at every code point boundary.</summary>
    internal static string GoReplace(string s, string oldS, string newS, long n)
    {
        if (oldS == newS || n == 0)
            return s;
        if (oldS.Length > 0)
        {
            var sb = new StringBuilder();
            int start = 0;
            long count = 0;
            while (n < 0 || count < n)
            {
                int idx = s.IndexOf(oldS, start, StringComparison.Ordinal);
                if (idx < 0) break;
                sb.Append(s, start, idx - start).Append(newS);
                start = idx + oldS.Length;
                count++;
            }
            sb.Append(s, start, s.Length - start);
            return sb.ToString();
        }
        else
        {
            // Empty match at the start and after each code point, up to n times.
            var sb = new StringBuilder();
            int i = 0;
            long count = 0;
            if (n < 0 || count < n)
            {
                sb.Append(newS);
                count++;
            }
            while (i < s.Length)
            {
                int start = i;
                CodePoints.Next(s, ref i);
                sb.Append(s, start, i - start);
                if ((n < 0 || count < n) && i < s.Length)
                {
                    sb.Append(newS);
                    count++;
                }
                else if ((n < 0 || count < n) && i == s.Length)
                {
                    sb.Append(newS);
                    count++;
                }
            }
            return sb.ToString();
        }
    }

    private static CelValue Split(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 2 || a.Length > 3 || a[0] is not StringValue s || a[1] is not StringValue sep
            || (a.Length == 3 && a[2] is not IntValue))
            return ErrorValue.NoSuchOverload(FunctionNames.Split, a);
        long n = a.Length == 3 ? ((IntValue)a[2]).Value : -1;
        ctx.Consume(s.Value.Length);
        var parts = GoSplitN(s.Value, sep.Value, n);
        var values = new CelValue[parts.Count];
        for (int i = 0; i < values.Length; i++) values[i] = StringValue.Of(parts[i]);
        return new ListValue(values);
    }

    /// <summary>Go's <c>strings.SplitN</c>.</summary>
    internal static List<string> GoSplitN(string s, string sep, long n)
    {
        var result = new List<string>();
        if (n == 0)
            return result;
        if (sep.Length == 0)
        {
            // Split into code points, the last element holding the remainder.
            int i = 0;
            while (i < s.Length)
            {
                if (n > 0 && result.Count == n - 1)
                {
                    result.Add(s.Substring(i));
                    return result;
                }
                int start = i;
                CodePoints.Next(s, ref i);
                result.Add(s.Substring(start, i - start));
            }
            return result;
        }
        int pos = 0;
        while (n < 0 || result.Count < n - 1)
        {
            int idx = s.IndexOf(sep, pos, StringComparison.Ordinal);
            if (idx < 0) break;
            result.Add(s.Substring(pos, idx - pos));
            pos = idx + sep.Length;
        }
        result.Add(s.Substring(pos));
        return result;
    }

    private static CelValue Substring(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 2 || a.Length > 3 || a[0] is not StringValue s || a[1] is not IntValue start
            || (a.Length == 3 && a[2] is not IntValue))
            return ErrorValue.NoSuchOverload(FunctionNames.Substring, a);
        var runes = CodePoints.Of(s.Value);
        ctx.Consume(runes.Length);
        long begin = start.Value;
        long end = a.Length == 3 ? ((IntValue)a[2]).Value : runes.Length;
        if (a.Length == 3 && begin > end)
            return new ErrorValue("invalid substring range. start: " + begin + ", end: " + end);
        if (begin < 0 || begin > runes.Length)
            return new ErrorValue("index out of range: " + begin);
        if (end < 0 || end > runes.Length)
            return new ErrorValue("index out of range: " + end);
        return StringValue.Of(CodePoints.ToString(runes, (int)begin, (int)(end - begin)));
    }

    private static CelValue Trim(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1 || a[0] is not StringValue s)
            return ErrorValue.NoSuchOverload(FunctionNames.Trim, a);
        return StringValue.Of(GoTrimSpace(s.Value));
    }

    /// <summary>Go's <c>strings.TrimSpace</c>: Unicode White_Space on both ends.</summary>
    internal static string GoTrimSpace(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsGoSpace(s[start])) start++;
        while (end > start && IsGoSpace(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    private static bool IsGoSpace(char c)
    {
        // unicode.IsSpace: '\t', '\n', '\v', '\f', '\r', ' ', U+0085, U+00A0 and the White_Space property.
        return c switch
        {
            '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u0085' or ' ' => true,
            _ => c > 0xff && char.IsWhiteSpace(c),
        };
    }

    private static CelValue Quote(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1 || a[0] is not StringValue s)
            return ErrorValue.NoSuchOverload(FunctionNames.StringsQuote, a);
        var sb = new StringBuilder(s.Value.Length + 2).Append('"');
        foreach (var c in Sanitize(s.Value))
        {
            switch (c)
            {
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }
        return StringValue.Of(sb.Append('"').ToString());
    }

    /// <summary>Replaces invalid UTF-16 sequences with U+FFFD, as the reference does for invalid UTF-8.</summary>
    private static string Sanitize(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool valid = !char.IsSurrogate(c)
                || (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]));
            if (valid)
            {
                if (char.IsHighSurrogate(c))
                {
                    sb?.Append(c).Append(s[i + 1]);
                    i++;
                }
                else
                {
                    sb?.Append(c);
                }
                continue;
            }
            sb ??= new StringBuilder(s, 0, i, s.Length);
            sb.Append('�');
        }
        return sb?.ToString() ?? s;
    }

    private static CelValue Join(EvalContext ctx, CelValue[] a)
    {
        if (a.Length < 1 || a.Length > 2 || a[0] is not ListValue list || (a.Length == 2 && a[1] is not StringValue))
            return ErrorValue.NoSuchOverload(FunctionNames.Join, a);
        var sep = a.Length == 2 ? ((StringValue)a[1]).Value : "";
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not StringValue s)
                return ErrorValue.NoSuchOverload(FunctionNames.Join, a);
            if (i > 0) sb.Append(sep);
            sb.Append(s.Value);
        }
        ctx.Consume(sb.Length);
        return StringValue.Of(sb.ToString());
    }

    private static CelValue Reverse(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1 || a[0] is not StringValue s)
            return ErrorValue.NoSuchOverload(FunctionNames.Reverse, a);
        var cps = CodePoints.Of(s.Value);
        Array.Reverse(cps);
        return StringValue.Of(CodePoints.ToString(cps, 0, cps.Length));
    }

    // ---- format ----

    private static CelValue Format(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2 || a[0] is not StringValue format || a[1] is not ListValue args)
            return ErrorValue.NoSuchOverload(FunctionNames.Format, a);
        ctx.Consume(format.Value.Length + args.Count);
        var result = StringFormatter.Format(format.Value, args.Elements, out var error);
        return error != null ? error : StringValue.Of(result!);
    }
}

/// <summary>The <c>string.format</c> clause grammar and renderers of the strings extension.</summary>
internal static class StringFormatter
{
    private const int DefaultPrecision = 6;

    public static string? Format(string format, IReadOnlyList<CelValue> args, out ErrorValue? error)
    {
        error = null;
        var sb = new StringBuilder();
        int i = 0;
        int argIndex = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                sb.Append('%');
                i += 2;
                continue;
            }
            if (argIndex >= args.Count)
            {
                error = new ErrorValue("index " + argIndex + " out of range");
                return null;
            }
            if (i + 1 >= format.Length)
            {
                error = new ErrorValue("unexpected end of string");
                return null;
            }
            i++;
            int? precision = null;
            if (format[i] == '.')
            {
                i++;
                int start = i;
                while (i < format.Length && char.IsAsciiDigit(format[i])) i++;
                if (i >= format.Length)
                {
                    error = new ErrorValue("could not parse formatting clause: error while parsing precision: could not find end of precision specifier");
                    return null;
                }
                if (i == start || !int.TryParse(format.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var p))
                {
                    error = new ErrorValue("could not parse formatting clause: error while parsing precision: error while converting precision to integer");
                    return null;
                }
                precision = p;
            }
            char clause = format[i++];
            var arg = args[argIndex++];
            string? rendered = clause switch
            {
                's' => RenderString(arg, out error),
                'd' => RenderDecimal(arg, out error),
                'f' => RenderFixed(arg, precision ?? DefaultPrecision, out error),
                'e' => RenderScientific(arg, precision ?? DefaultPrecision, out error),
                'b' => RenderBinary(arg, out error),
                'x' => RenderHex(arg, false, out error),
                'X' => RenderHex(arg, true, out error),
                'o' => RenderOctal(arg, out error),
                _ => null,
            };
            if (error != null)
            {
                error = new ErrorValue("error during formatting: " + error.Message);
                return null;
            }
            if (rendered == null)
            {
                error = new ErrorValue("could not parse formatting clause: unrecognized formatting clause \"" + clause + "\"");
                return null;
            }
            sb.Append(rendered);
        }
        return sb.ToString();
    }

    private static string? RenderString(CelValue arg, out ErrorValue? error)
    {
        error = null;
        switch (arg)
        {
            case ListValue list:
                return RenderList(list, out error);
            case MapValue map:
                return RenderMap(map, out error);
            case NullValue:
                return "null";
            case DoubleValue d:
                return FormatSpecial(d.Value) ?? GoFormat.FormatDoubleG(d.Value);
            case IntValue or UintValue or BoolValue or StringValue or BytesValue or TimestampValue or DurationValue or TypeValue:
            {
                var s = StandardFunctions.ConvertToString(arg, out error);
                return error != null ? null : s;
            }
            default:
                error = new ErrorValue("string clause can only be used on strings, bools, bytes, ints, doubles, maps, lists, types, durations, and timestamps, was given " + arg.TypeName);
                return null;
        }
    }

    private static string? FormatSpecial(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        return null;
    }

    private static string? RenderList(ListValue list, out ErrorValue? error)
    {
        error = null;
        var sb = new StringBuilder("[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var s = RenderElement(list[i], out error);
            if (error != null) return null;
            sb.Append(s);
        }
        return sb.Append(']').ToString();
    }

    private static string? RenderMap(MapValue map, out ErrorValue? error)
    {
        error = null;
        var pairs = new List<(string Key, string Value)>(map.Count);
        foreach (var entry in map.Entries)
        {
            string? key;
            switch (entry.Key)
            {
                case StringValue or BoolValue:
                    key = StandardFunctions.ConvertToString(entry.Key, out error);
                    break;
                case IntValue or UintValue:
                    key = RenderDecimal(entry.Key, out error);
                    break;
                default:
                    error = new ErrorValue("no formatting function for map key of type " + entry.Key.TypeName);
                    return null;
            }
            if (error != null) return null;
            var value = RenderElement(entry.Value, out error);
            if (error != null) return null;
            pairs.Add((key!, value!));
        }
        pairs.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        var sb = new StringBuilder("{");
        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(pairs[i].Key).Append(": ").Append(pairs[i].Value);
        }
        return sb.Append('}').ToString();
    }

    private static string? RenderElement(CelValue v, out ErrorValue? error)
    {
        error = null;
        switch (v)
        {
            case IntValue or UintValue:
                return RenderDecimal(v, out error);
            case DoubleValue d:
                return FormatSpecial(d.Value) ?? GoFormat.FormatDoubleG(d.Value);
            case StringValue or BytesValue or BoolValue or NullValue or TypeValue or TimestampValue or DurationValue:
                return RenderString(v, out error);
            case ListValue l:
                return RenderList(l, out error);
            case MapValue m:
                return RenderMap(m, out error);
            default:
                error = new ErrorValue("no formatting function for " + v.TypeName);
                return null;
        }
    }

    private static string? RenderDecimal(CelValue arg, out ErrorValue? error)
    {
        error = null;
        switch (arg)
        {
            case IntValue i:
                return i.Value.ToString(CultureInfo.InvariantCulture);
            case UintValue u:
                return u.Value.ToString(CultureInfo.InvariantCulture);
            case DoubleValue d when FormatSpecial(d.Value) != null:
                return FormatSpecial(d.Value);
            default:
                error = new ErrorValue("decimal clause can only be used on integers, was given " + arg.TypeName);
                return null;
        }
    }

    private static double? NumericArg(CelValue arg)
    {
        return arg switch
        {
            DoubleValue d => d.Value,
            IntValue i => i.Value,
            UintValue u => u.Value,
            StringValue s when s.Value is "NaN" => double.NaN,
            StringValue s when s.Value is "Infinity" => double.PositiveInfinity,
            StringValue s when s.Value is "-Infinity" => double.NegativeInfinity,
            _ => null,
        };
    }

    private static string? RenderFixed(CelValue arg, int precision, out ErrorValue? error)
    {
        error = null;
        var d = NumericArg(arg);
        if (d == null)
        {
            error = new ErrorValue("fixed-point clause can only be used on doubles, was given " + arg.TypeName);
            return null;
        }
        return FormatSpecial(d.Value) ?? GoFormat.FormatDoubleF(d.Value, precision);
    }

    private static string? RenderScientific(CelValue arg, int precision, out ErrorValue? error)
    {
        error = null;
        var d = NumericArg(arg);
        if (d == null)
        {
            error = new ErrorValue("scientific clause can only be used on doubles, was given " + arg.TypeName);
            return null;
        }
        return FormatSpecial(d.Value) ?? GoFormat.FormatDoubleE(d.Value, precision);
    }

    private static string? RenderBinary(CelValue arg, out ErrorValue? error)
    {
        error = null;
        switch (arg)
        {
            case IntValue i:
                return i.Value < 0 ? "-" + Convert.ToString(-(i.Value == long.MinValue ? i.Value : i.Value), 2).TrimStart('-') : Convert.ToString(i.Value, 2);
            case UintValue u:
                return Convert.ToString(unchecked((long)u.Value), 2).PadLeft(u.Value > long.MaxValue ? 64 : 0, '0');
            case BoolValue b:
                return b.Value ? "1" : "0";
            default:
                error = new ErrorValue("only integers and bools can be formatted as binary, was given " + arg.TypeName);
                return null;
        }
    }

    private static string? RenderHex(CelValue arg, bool upper, out ErrorValue? error)
    {
        error = null;
        string? result = arg switch
        {
            IntValue i => i.Value < 0 ? "-" + ((ulong)(-(i.Value + 1)) + 1).ToString("x", CultureInfo.InvariantCulture) : i.Value.ToString("x", CultureInfo.InvariantCulture),
            UintValue u => u.Value.ToString("x", CultureInfo.InvariantCulture),
            StringValue s => Convert.ToHexString(Encoding.UTF8.GetBytes(s.Value)).ToLowerInvariant(),
            BytesValue b => Convert.ToHexString(b.Value).ToLowerInvariant(),
            _ => null,
        };
        if (result == null)
        {
            error = new ErrorValue("only integers, byte buffers, and strings can be formatted as hex, was given " + arg.TypeName);
            return null;
        }
        return upper ? result.ToUpperInvariant() : result;
    }

    private static string? RenderOctal(CelValue arg, out ErrorValue? error)
    {
        error = null;
        switch (arg)
        {
            case IntValue i:
                return i.Value < 0 ? "-" + Convert.ToString(-(i.Value + 1) + 1, 8) : Convert.ToString(i.Value, 8);
            case UintValue u:
                return Convert.ToString(unchecked((long)u.Value), 8);
            default:
                error = new ErrorValue("octal clause can only be used on integers, was given " + arg.TypeName);
                return null;
        }
    }
}
