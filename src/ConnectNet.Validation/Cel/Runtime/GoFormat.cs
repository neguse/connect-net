using System;
using System.Globalization;
using System.Text;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>
/// Number, timestamp and duration text conversions with the exact semantics of the reference
/// implementation (Go's strconv and time packages), which the conformance corpus pins.
/// </summary>
internal static class GoFormat
{
    /// <summary>Decomposes a finite non-zero double into its shortest round-trip decimal digits and exponent.</summary>
    private static (string Digits, int DecimalPoint) ShortestDigits(double value)
    {
        // "R" yields the shortest digit string that round-trips; "E16" would not be shortest.
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int exp = 0;
        int e = text.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exp = int.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text.Substring(0, e);
        }
        int dot = text.IndexOf('.');
        string intPart = dot >= 0 ? text.Substring(0, dot) : text;
        string fracPart = dot >= 0 ? text.Substring(dot + 1) : "";
        var digits = (intPart + fracPart).TrimStart('0');
        int leadingZeros = (intPart + fracPart).Length - digits.Length;
        // decimal point position relative to the start of `digits`
        int decimalPoint = intPart.Length - leadingZeros + exp;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
            digits = "0";
        return (digits, decimalPoint);
    }

    /// <summary>Go's <c>%g</c> with the shortest representation (the CEL <c>string(double)</c> conversion).</summary>
    public static string FormatDoubleG(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        if (value == 0)
            return double.IsNegative(value) ? "-0" : "0";

        var (digits, decimalPoint) = ShortestDigits(value);
        int exp = decimalPoint - 1;
        var sb = new StringBuilder();
        if (value < 0) sb.Append('-');
        if (exp < -4 || exp >= 21)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            AppendExponent(sb, exp);
        }
        else
        {
            AppendFixed(sb, digits, decimalPoint);
        }
        return sb.ToString();
    }

    private static void AppendFixed(StringBuilder sb, string digits, int decimalPoint)
    {
        if (decimalPoint <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -decimalPoint);
            sb.Append(digits);
        }
        else if (decimalPoint >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', decimalPoint - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, decimalPoint);
            sb.Append('.');
            sb.Append(digits, decimalPoint, digits.Length - decimalPoint);
        }
    }

    private static void AppendExponent(StringBuilder sb, int exp)
    {
        sb.Append('e').Append(exp < 0 ? '-' : '+');
        int a = Math.Abs(exp);
        if (a < 10) sb.Append('0');
        sb.Append(a.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Go's <c>%.Nf</c>.</summary>
    public static string FormatDoubleF(double value, int precision)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        // .NET's fixed-point formatting rounds the exact binary value correctly, as Go does.
        return value.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Go's <c>%.Ne</c>: a two-digit minimum exponent.</summary>
    public static string FormatDoubleE(double value, int precision)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        var text = value.ToString("E" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        // .NET writes E+003; Go writes e+03.
        int e = text.IndexOf('E');
        var mantissa = text.Substring(0, e);
        var sign = text[e + 1];
        var expDigits = text.Substring(e + 2).TrimStart('0');
        if (expDigits.Length < 2) expDigits = expDigits.PadLeft(2, '0');
        return mantissa + "e" + sign + expDigits;
    }

    /// <summary>Go's <c>strconv.ParseFloat(s, 64)</c>: decimal and hexadecimal floats, inf and nan, no spaces; overflow fails.</summary>
    public static bool TryParseDouble(string s, out double value)
    {
        value = 0;
        if (s.Length == 0) return false;
        var lower = s.ToLowerInvariant();
        var unsigned = lower.StartsWith("+", StringComparison.Ordinal) || lower.StartsWith("-", StringComparison.Ordinal) ? lower.Substring(1) : lower;
        bool negative = lower[0] == '-';
        switch (unsigned)
        {
            case "inf":
            case "infinity":
                value = negative ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            case "nan":
                if (lower[0] == '+' || lower[0] == '-') return false;
                value = double.NaN;
                return true;
        }
        if (unsigned.StartsWith("0x", StringComparison.Ordinal))
            return TryParseHexFloat(unsigned.Substring(2), negative, out value);

        // Validate Go's decimal float syntax: digits [. digits] [e [+-] digits], underscores not allowed.
        int i = 0;
        int digits = 0;
        while (i < unsigned.Length && char.IsAsciiDigit(unsigned[i])) { i++; digits++; }
        if (i < unsigned.Length && unsigned[i] == '.')
        {
            i++;
            while (i < unsigned.Length && char.IsAsciiDigit(unsigned[i])) { i++; digits++; }
        }
        if (digits == 0) return false;
        if (i < unsigned.Length && unsigned[i] == 'e')
        {
            i++;
            if (i < unsigned.Length && (unsigned[i] == '+' || unsigned[i] == '-')) i++;
            int expDigits = 0;
            while (i < unsigned.Length && char.IsAsciiDigit(unsigned[i])) { i++; expDigits++; }
            if (expDigits == 0) return false;
        }
        if (i != unsigned.Length) return false;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        // Go reports a range error for values that overflow to infinity.
        return !double.IsInfinity(value);
    }

    private static bool TryParseHexFloat(string s, bool negative, out double value)
    {
        value = 0;
        // mantissa hex digits with optional '.', then mandatory 'p' exponent.
        int p = s.IndexOf('p');
        if (p < 0) return false;
        var mantissa = s.Substring(0, p);
        if (!int.TryParse(s.Substring(p + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exp))
            return false;
        int dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa.Substring(0, dot) : mantissa;
        var fracPart = dot >= 0 ? mantissa.Substring(dot + 1) : "";
        if (intPart.Length + fracPart.Length == 0) return false;
        double m = 0;
        foreach (var c in intPart + fracPart)
        {
            if (!Uri.IsHexDigit(c)) return false;
            m = m * 16 + Convert.ToInt32(c.ToString(), 16);
        }
        value = m * Math.Pow(2, exp - 4 * fracPart.Length);
        if (negative) value = -value;
        return !double.IsInfinity(value);
    }

    /// <summary>Go's <c>strconv.ParseInt(s, 10, 64)</c>: optional sign, decimal digits only.</summary>
    public static bool TryParseInt64(string s, out long value)
    {
        value = 0;
        if (s.Length == 0) return false;
        int i = 0;
        bool negative = false;
        if (s[0] == '+' || s[0] == '-')
        {
            negative = s[0] == '-';
            i = 1;
        }
        if (i == s.Length) return false;
        ulong magnitude = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (!char.IsAsciiDigit(c)) return false;
            ulong d = (ulong)(c - '0');
            if (magnitude > (ulong.MaxValue - d) / 10) return false;
            magnitude = magnitude * 10 + d;
        }
        if (negative)
        {
            if (magnitude > (ulong)long.MaxValue + 1) return false;
            value = magnitude == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
            return true;
        }
        if (magnitude > long.MaxValue) return false;
        value = (long)magnitude;
        return true;
    }

    /// <summary>Go's <c>strconv.ParseUint(s, 10, 64)</c>: decimal digits only, no sign.</summary>
    public static bool TryParseUint64(string s, out ulong value)
    {
        value = 0;
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c)) return false;
            ulong d = (ulong)(c - '0');
            if (value > (ulong.MaxValue - d) / 10) return false;
            value = value * 10 + d;
        }
        return true;
    }

    /// <summary>Go's <c>strconv.ParseBool</c>.</summary>
    public static bool TryParseBool(string s, out bool value)
    {
        switch (s)
        {
            case "1": case "t": case "T": case "true": case "TRUE": case "True":
                value = true;
                return true;
            case "0": case "f": case "F": case "false": case "FALSE": case "False":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    // ---- durations ----

    /// <summary>
    /// Go's <c>time.ParseDuration</c>: a signed sequence of decimal numbers with unit suffixes
    /// (ns, us, µs, ms, s, m, h); "0" alone is permitted. Returns false on syntax or overflow.
    /// </summary>
    public static bool TryParseDuration(string s, out long nanoseconds)
    {
        nanoseconds = 0;
        var orig = s;
        bool negative = false;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
        {
            negative = s[0] == '-';
            s = s.Substring(1);
        }
        if (s == "0")
            return true;
        if (s.Length == 0)
            return false;

        ulong total = 0;
        while (s.Length > 0)
        {
            // integer part
            ulong v = 0;
            int pl = s.Length;
            int i = 0;
            bool hasInt = false;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                hasInt = true;
                ulong d = (ulong)(s[i] - '0');
                if (v > (ulong.MaxValue - d) / 10) return false;
                v = v * 10 + d;
                i++;
            }
            // fraction
            ulong f = 0;
            double scale = 1;
            bool hasFrac = false;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    hasFrac = true;
                    if (f < (ulong.MaxValue - 9) / 10)
                    {
                        f = f * 10 + (ulong)(s[i] - '0');
                        scale *= 10;
                    }
                    i++;
                }
            }
            if (!hasInt && !hasFrac) return false;
            // unit
            int ustart = i;
            while (i < s.Length && s[i] != '.' && !char.IsAsciiDigit(s[i])) i++;
            var unit = s.Substring(ustart, i - ustart);
            if (unit.Length == 0) return false;
            ulong unitNanos = unit switch
            {
                "ns" => 1,
                "us" or "µs" or "μs" => 1_000,
                "ms" => 1_000_000,
                "s" => 1_000_000_000,
                "m" => 60_000_000_000,
                "h" => 3_600_000_000_000,
                _ => 0,
            };
            if (unitNanos == 0) return false;
            if (v > (1UL << 63) / unitNanos) return false;
            ulong part = v * unitNanos;
            if (f > 0)
            {
                part += (ulong)(f * (unitNanos / scale));
                if (part > 1UL << 63) return false;
            }
            total += part;
            if (total > 1UL << 63) return false;
            s = s.Substring(i);
            _ = pl;
        }
        if (negative)
        {
            if (total > 1UL << 63) return false;
            nanoseconds = total == 1UL << 63 ? long.MinValue : -(long)total;
            return true;
        }
        if (total > long.MaxValue) return false;
        nanoseconds = (long)total;
        return true;
    }

    /// <summary>The CEL string form of a duration: seconds with the shortest fraction and an "s" suffix.</summary>
    public static string FormatDuration(long nanoseconds)
    {
        // Go: strconv.FormatFloat(d.Seconds(), 'f', -1, 64) + "s"
        double seconds = nanoseconds / 1e9;
        long whole = nanoseconds / DurationValue.NanosPerSecond;
        long frac = nanoseconds % DurationValue.NanosPerSecond;
        // Exact when representable; d.Seconds() in Go is sec + nsec/1e9 as float64.
        seconds = whole + frac / 1e9;
        return FormatDoubleShortestF(seconds) + "s";
    }

    /// <summary>Go's <c>strconv.FormatFloat(f, 'f', -1, 64)</c>.</summary>
    public static string FormatDoubleShortestF(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";
        var (digits, decimalPoint) = ShortestDigits(value);
        var sb = new StringBuilder();
        if (value < 0) sb.Append('-');
        AppendFixed(sb, digits, decimalPoint);
        return sb.ToString();
    }

    // ---- timestamps ----

    private static bool InRange(string s, int lo, int hi)
    {
        int v = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c)) return false;
            v = v * 10 + (c - '0');
        }
        return v >= lo && v <= hi;
    }

    private static bool IsChar(char got, char want) => got == want || char.ToLowerInvariant(got) == want;

    /// <summary>The strict RFC 3339 shape the reference implementation requires before parsing.</summary>
    public static bool IsStrictRfc3339(string s)
    {
        if (s.Length < 20) return false;
        if (!InRange(s.Substring(0, 4), 0, 9999) || !IsChar(s[4], '-') || !InRange(s.Substring(5, 2), 1, 12)
            || !IsChar(s[7], '-') || !InRange(s.Substring(8, 2), 1, 31) || !IsChar(s[10], 't')
            || !InRange(s.Substring(11, 2), 0, 23) || !IsChar(s[13], ':') || !InRange(s.Substring(14, 2), 0, 59)
            || !IsChar(s[16], ':') || !InRange(s.Substring(17, 2), 0, 60))
            return false;
        var rest = s.Substring(19);
        if (rest[0] == '.')
        {
            rest = rest.Substring(1);
            int n = 0;
            while (n < rest.Length && char.IsAsciiDigit(rest[n])) n++;
            if (n == 0) return false;
            rest = rest.Substring(n);
        }
        if (rest.Length == 1)
            return IsChar(rest[0], 'z');
        if (rest.Length == 6 && (rest[0] == '+' || rest[0] == '-'))
            return InRange(rest.Substring(1, 2), 0, 23) && IsChar(rest[3], ':') && InRange(rest.Substring(4, 2), 0, 59);
        return false;
    }

    /// <summary>Parses an RFC 3339 timestamp into Unix seconds and nanoseconds; false when malformed or out of range.</summary>
    public static bool TryParseTimestamp(string s, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;
        if (!IsStrictRfc3339(s)) return false;
        int year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        int month = int.Parse(s.AsSpan(5, 2), CultureInfo.InvariantCulture);
        int day = int.Parse(s.AsSpan(8, 2), CultureInfo.InvariantCulture);
        int hour = int.Parse(s.AsSpan(11, 2), CultureInfo.InvariantCulture);
        int minute = int.Parse(s.AsSpan(14, 2), CultureInfo.InvariantCulture);
        int second = int.Parse(s.AsSpan(17, 2), CultureInfo.InvariantCulture);
        int i = 19;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            var frac = s.Substring(start, i - start);
            // Go keeps at most nanosecond precision, truncating further digits.
            if (frac.Length > 9) frac = frac.Substring(0, 9);
            nanos = int.Parse(frac.PadRight(9, '0'), CultureInfo.InvariantCulture);
        }
        int offsetSeconds = 0;
        if (s[i] != 'Z' && s[i] != 'z')
        {
            int oh = int.Parse(s.AsSpan(i + 1, 2), CultureInfo.InvariantCulture);
            int om = int.Parse(s.AsSpan(i + 4, 2), CultureInfo.InvariantCulture);
            offsetSeconds = (oh * 60 + om) * 60;
            if (s[i] == '-') offsetSeconds = -offsetSeconds;
        }
        if (year == 0 || day > DateTime.DaysInMonth(Math.Max(year, 1), month))
        {
            // Year 0 is outside the representable range; the reference reports a range error
            // for it rather than a parse error, which the caller treats the same way.
            if (year == 0)
            {
                seconds = TimestampValue.MinUnixSeconds - 1;
                return true;
            }
            return false;
        }
        if (second == 60)
        {
            // Go rejects leap seconds in time.Parse ("second out of range").
            return false;
        }
        var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        seconds = (dt.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond - offsetSeconds;
        return true;
    }

    /// <summary>RFC 3339 with nanoseconds in UTC, trailing fraction zeros trimmed (Go's RFC3339Nano).</summary>
    public static string FormatTimestamp(TimestampValue t)
    {
        var dt = DateTime.UnixEpoch.AddSeconds(t.Seconds);
        var sb = new StringBuilder(32);
        sb.Append(dt.Year.ToString("D4", CultureInfo.InvariantCulture)).Append('-')
            .Append(dt.Month.ToString("D2", CultureInfo.InvariantCulture)).Append('-')
            .Append(dt.Day.ToString("D2", CultureInfo.InvariantCulture)).Append('T')
            .Append(dt.Hour.ToString("D2", CultureInfo.InvariantCulture)).Append(':')
            .Append(dt.Minute.ToString("D2", CultureInfo.InvariantCulture)).Append(':')
            .Append(dt.Second.ToString("D2", CultureInfo.InvariantCulture));
        if (t.Nanos != 0)
        {
            var frac = t.Nanos.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
            sb.Append('.').Append(frac);
        }
        return sb.Append('Z').ToString();
    }
}
