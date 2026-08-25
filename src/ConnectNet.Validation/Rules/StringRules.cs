using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Buf.Validate;
using ConnectNet.Validation.Internal;

namespace ConnectNet.Validation.Rules;

internal static class StringRuleEvaluator
{
    // Hard cap on regex execution time. ReDoS-prone patterns or pathological inputs (from
    // a proto definer combined with attacker-controlled values) cannot stall a worker
    // thread for more than this interval.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex UuidRegex = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex TuuidRegex = new(
        "^[0-9a-fA-F]{32}$",
        RegexOptions.Compiled, RegexTimeout);

    public static void Evaluate(StringRules rules, string value, string path, ViolationCollector violations)
    {
        // const
        if (rules.HasConst && value != rules.Const)
        {
            violations.Add(new Violation(
                path,
                "string.const",
                $"value must equal \"{rules.Const}\"",
                value));
        }

        // len / min_len / max_len count Unicode code points (protovalidate semantics),
        // not UTF-16 code units. Computed lazily only when a length rule is present.
        if (rules.HasLen || rules.HasMinLen || rules.HasMaxLen)
        {
            var codePoints = (ulong)CountCodePoints(value);

            if (rules.HasLen && codePoints != rules.Len)
            {
                violations.Add(new Violation(
                    path,
                    "string.len",
                    $"value length must be {rules.Len}",
                    value));
            }

            if (rules.HasMinLen && codePoints < rules.MinLen)
            {
                violations.Add(new Violation(
                    path,
                    "string.min_len",
                    $"value length must be at least {rules.MinLen}",
                    value));
            }

            if (rules.HasMaxLen && codePoints > rules.MaxLen)
            {
                violations.Add(new Violation(
                    path,
                    "string.max_len",
                    $"value length must be at most {rules.MaxLen}",
                    value));
            }
        }

        // len_bytes / min_bytes / max_bytes count UTF-8 bytes.
        if (rules.HasLenBytes || rules.HasMinBytes || rules.HasMaxBytes)
        {
            var byteCount = (ulong)Encoding.UTF8.GetByteCount(value);

            if (rules.HasLenBytes && byteCount != rules.LenBytes)
            {
                violations.Add(new Violation(
                    path,
                    "string.len_bytes",
                    $"value length must be {rules.LenBytes} bytes",
                    value));
            }

            if (rules.HasMinBytes && byteCount < rules.MinBytes)
            {
                violations.Add(new Violation(
                    path,
                    "string.min_bytes",
                    $"value length must be at least {rules.MinBytes} bytes",
                    value));
            }

            if (rules.HasMaxBytes && byteCount > rules.MaxBytes)
            {
                violations.Add(new Violation(
                    path,
                    "string.max_bytes",
                    $"value length must be at most {rules.MaxBytes} bytes",
                    value));
            }
        }

        // pattern
        if (rules.HasPattern)
        {
            try
            {
                // Construct a Regex with explicit timeout so user-supplied patterns combined
                // with hostile inputs can never spin a thread indefinitely (ReDoS).
                var re = new Regex(rules.Pattern, RegexOptions.None, RegexTimeout);
                if (!re.IsMatch(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.pattern",
                        $"value must match pattern \"{rules.Pattern}\"",
                        value));
                }
            }
            catch (ArgumentException)
            {
                violations.Add(new Violation(
                    path,
                    "string.pattern",
                    $"invalid regex pattern \"{rules.Pattern}\"",
                    value));
            }
            catch (RegexMatchTimeoutException)
            {
                violations.Add(new Violation(
                    path,
                    "string.pattern",
                    $"regex match timed out for pattern \"{rules.Pattern}\"",
                    value));
            }
        }

        // prefix
        if (rules.HasPrefix && !value.StartsWith(rules.Prefix, StringComparison.Ordinal))
        {
            violations.Add(new Violation(
                path,
                "string.prefix",
                $"value must have prefix \"{rules.Prefix}\"",
                value));
        }

        // suffix
        if (rules.HasSuffix && !value.EndsWith(rules.Suffix, StringComparison.Ordinal))
        {
            violations.Add(new Violation(
                path,
                "string.suffix",
                $"value must have suffix \"{rules.Suffix}\"",
                value));
        }

        // contains
        if (rules.HasContains && !value.Contains(rules.Contains))
        {
            violations.Add(new Violation(
                path,
                "string.contains",
                $"value must contain \"{rules.Contains}\"",
                value));
        }

        // not_contains
        if (rules.HasNotContains && value.Contains(rules.NotContains))
        {
            violations.Add(new Violation(
                path,
                "string.not_contains",
                $"value must not contain \"{rules.NotContains}\"",
                value));
        }

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "string.in",
                $"value must be in list [{string.Join(", ", rules.In)}]",
                value));
        }

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "string.not_in",
                $"value must not be in list [{string.Join(", ", rules.NotIn)}]",
                value));
        }

        // Well-known format validations
        switch (rules.WellKnownCase)
        {
            case StringRules.WellKnownOneofCase.Email when rules.Email:
                if (!IsValidEmail(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.email",
                        "value must be a valid email address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Hostname when rules.Hostname:
                if (!IsValidHostname(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.hostname",
                        "value must be a valid hostname",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ip when rules.Ip:
                if (!IsValidIpv4(value) && !IsValidIpv6(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.ip",
                        "value must be a valid IP address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ipv4 when rules.Ipv4:
                if (!IsValidIpv4(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.ipv4",
                        "value must be a valid IPv4 address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ipv6 when rules.Ipv6:
                if (!IsValidIpv6(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.ipv6",
                        "value must be a valid IPv6 address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Uri when rules.Uri:
                if (!UriValidation.IsValidUri(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.uri",
                        "value must be a valid URI",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.UriRef when rules.UriRef:
                if (!UriValidation.IsValidUriRef(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.uri_ref",
                        "value must be a valid URI reference",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Uuid when rules.Uuid:
                if (!SafeIsMatch(UuidRegex, value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.uuid",
                        "value must be a valid UUID",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Tuuid when rules.Tuuid:
                if (!SafeIsMatch(TuuidRegex, value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.tuuid",
                        "value must be a valid trimmed UUID",
                        value));
                }
                break;
        }
    }

    /// <summary>Counts Unicode code points; a surrogate pair counts as one.</summary>
    internal static int CountCodePoints(string value)
    {
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// protovalidate email semantics: total length &lt;= 254, exactly one '@', a dot-atom
    /// local part of at most 64 characters, and a valid hostname domain (single-label
    /// domains such as <c>a@b</c> are allowed).
    /// </summary>
    internal static bool IsValidEmail(string value)
    {
        if (value.Length == 0 || value.Length > 254)
            return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
            return false;

        var local = value.Substring(0, at);
        var domain = value.Substring(at + 1);

        if (local.Length > 64)
            return false;

        return IsValidEmailLocalPart(local) && IsValidHostname(domain);
    }

    private static bool IsValidEmailLocalPart(string local)
    {
        // dot-atom: atext+ ("." atext+)* — no leading/trailing/consecutive dots.
        if (local.Length == 0 || local[0] == '.' || local[local.Length - 1] == '.')
            return false;

        var prevDot = false;
        foreach (var c in local)
        {
            if (c == '.')
            {
                if (prevDot)
                    return false;
                prevDot = true;
                continue;
            }
            prevDot = false;
            if (!IsAtext(c))
                return false;
        }
        return true;
    }

    private static bool IsAtext(char c)
    {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            return true;
        return "!#$%&'*+-/=?^_`{|}~".IndexOf(c) >= 0;
    }

    /// <summary>
    /// protovalidate hostname semantics (RFC 1034 preferred syntax): total length &lt;= 253,
    /// labels of 1-63 characters from [A-Za-z0-9-] that neither start nor end with a hyphen,
    /// an optional trailing dot, and a final label that is not all digits.
    /// </summary>
    internal static bool IsValidHostname(string value)
    {
        if (value.Length == 0 || value.Length > 253)
            return false;

        var s = value[value.Length - 1] == '.' ? value.Substring(0, value.Length - 1) : value;
        if (s.Length == 0)
            return false;

        var allDigits = false;
        foreach (var label in s.Split('.'))
        {
            allDigits = true;
            if (label.Length == 0 || label.Length > 63)
                return false;
            if (label[0] == '-' || label[label.Length - 1] == '-')
                return false;
            foreach (var c in label)
            {
                var isDigit = c >= '0' && c <= '9';
                if (!isDigit && !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-'))
                    return false;
                allDigits = allDigits && isDigit;
            }
        }

        // The last label cannot be all digits (e.g. "example.123" is not a hostname).
        return !allDigits;
    }

    /// <summary>
    /// Strict dotted-quad IPv4: exactly four decimal octets 0-255 with no leading zeros.
    /// Deliberately rejects the shorthand and octal forms .NET's IPAddress.TryParse accepts
    /// ("1", "127.1", "010.1.1.1").
    /// </summary>
    internal static bool IsValidIpv4(string value)
    {
        int octets = 0;
        int i = 0;
        int n = value.Length;
        while (i < n)
        {
            if (octets == 4)
                return false;
            int start = i;
            int octet = 0;
            while (i < n && value[i] >= '0' && value[i] <= '9')
            {
                octet = octet * 10 + (value[i] - '0');
                i++;
                if (i - start > 3)
                    return false;
            }
            int digits = i - start;
            if (digits == 0)
                return false;
            if (digits > 1 && value[start] == '0') // leading zero
                return false;
            if (octet > 255)
                return false;
            octets++;
            if (i < n)
            {
                if (value[i] != '.')
                    return false;
                i++;
                if (i == n) // trailing dot
                    return false;
            }
        }
        return octets == 4;
    }

    /// <summary>
    /// RFC 4291 IPv6 text form only: hex groups separated by ':', at most one '::', and an
    /// optional embedded dotted-quad IPv4 tail. Brackets and zone identifiers ("%eth0") are
    /// rejected, unlike .NET's IPAddress.TryParse.
    /// </summary>
    internal static bool IsValidIpv6(string value)
    {
        if (value.Length == 0)
            return false;

        int n = value.Length;
        int doubleColon = value.IndexOf("::", StringComparison.Ordinal);
        if (doubleColon >= 0 && value.IndexOf("::", doubleColon + 1, StringComparison.Ordinal) >= 0)
            return false; // more than one "::"

        // "::" alone is valid.
        if (value == "::")
            return true;

        // A single leading/trailing ':' is only allowed as part of "::".
        if (value[0] == ':' && doubleColon != 0)
            return false;
        if (value[n - 1] == ':' && doubleColon != n - 2)
            return false;

        var head = doubleColon >= 0 ? value.Substring(0, doubleColon) : value;
        var tail = doubleColon >= 0 ? value.Substring(doubleColon + 2) : "";

        int groups = 0;
        bool hasIpv4Tail = false;

        // An embedded IPv4 tail is only allowed in the final component of the address:
        // the head is final only when there is no "::" at all.
        if (!ParseIpv6Part(head, allowIpv4Tail: doubleColon < 0, ref groups, ref hasIpv4Tail))
            return false;
        if (!ParseIpv6Part(tail, allowIpv4Tail: true, ref groups, ref hasIpv4Tail))
            return false;

        var total = groups + (hasIpv4Tail ? 2 : 0);
        return doubleColon >= 0 ? total <= 7 : total == 8;
    }

    private static bool ParseIpv6Part(string part, bool allowIpv4Tail, ref int groups, ref bool hasIpv4Tail)
    {
        if (part.Length == 0)
            return true;

        var pieces = part.Split(':');
        for (int i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            if (piece.Length == 0)
                return false; // empty group (":::", ":x", stray ':')

            var isLast = i == pieces.Length - 1;
            if (isLast && allowIpv4Tail && piece.IndexOf('.') >= 0)
            {
                // Embedded IPv4 tail must be the final component of the address.
                if (!IsValidIpv4(piece))
                    return false;
                hasIpv4Tail = true;
                continue;
            }

            if (piece.Length > 4)
                return false;
            foreach (var c in piece)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                    return false;
            }
            groups++;
        }

        return true;
    }

    private static bool SafeIsMatch(Regex regex, string value)
    {
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // Treat timeout as "no match"; the caller records a generic format violation.
            return false;
        }
    }
}

/// <summary>
/// RFC 3986-oriented URI validation. Not a full grammar parser, but strict enough to reject
/// the inputs .NET's Uri class silently normalizes or over-accepts: schemeless paths
/// promoted to file: URIs, whitespace, control characters, non-ASCII, and invalid
/// percent-encoding.
/// </summary>
internal static class UriValidation
{
    public static bool IsValidUri(string value)
    {
        // uri = scheme ":" hier-part [ "?" query ] [ "#" fragment ]
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;
        if (!IsAlpha(value[0]))
            return false;
        for (int i = 1; i < colon; i++)
        {
            if (!IsSchemeChar(value[i]))
                return false;
        }
        return ValidateBody(value, colon + 1);
    }

    public static bool IsValidUriRef(string value)
    {
        // The empty string is a valid same-document reference (RFC 3986 §4.4).
        if (value.Length == 0)
            return true;

        if (IsValidUri(value))
            return true;

        // relative-ref: the first path segment must not contain ':' (RFC 3986 path-noscheme);
        // anything with a ':' before '/', '?' or '#' had to parse as an absolute URI above.
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '/' || c == '?' || c == '#')
                break;
            if (c == ':')
                return false;
        }

        return ValidateBody(value, 0);
    }

    private static bool ValidateBody(string value, int start)
    {
        // Validates the character set of everything after the scheme: unreserved,
        // sub-delims, ':' '@' '/' '?' '#' '[' ']' and correctly formed percent-encoding.
        // ('?' and '#' ordering is not enforced; '[' ']' cover IPv6 literals in authority.)
        for (int i = start; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length)
                    return false;
                if (!IsHex(value[i + 1]) || !IsHex(value[i + 2]))
                    return false;
                i += 2;
                continue;
            }
            if (!IsUriChar(c))
                return false;
        }
        return true;
    }

    private static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsSchemeChar(char c)
        => IsAlpha(c) || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsUriChar(char c)
    {
        if (IsAlpha(c) || (c >= '0' && c <= '9'))
            return true;
        switch (c)
        {
            // unreserved
            case '-': case '.': case '_': case '~':
            // sub-delims
            case '!': case '$': case '&': case '\'': case '(': case ')':
            case '*': case '+': case ',': case ';': case '=':
            // pchar extras and structure
            case ':': case '@': case '/': case '?': case '#':
            case '[': case ']':
                return true;
            default:
                return false;
        }
    }
}
