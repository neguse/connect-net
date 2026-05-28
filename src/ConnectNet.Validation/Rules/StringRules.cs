using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class StringRuleEvaluator
{
    // Hard cap on regex execution time. ReDoS-prone patterns or pathological inputs (from
    // a proto definer combined with attacker-controlled values) cannot stall a worker
    // thread for more than this interval.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex HostnameRegex = new(@"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*$", RegexOptions.Compiled, RegexTimeout);

    public static void Evaluate(StringRules rules, string value, string path, List<Violation> violations)
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

        // len
        if (rules.HasLen && (ulong)value.Length != rules.Len)
        {
            violations.Add(new Violation(
                path,
                "string.len",
                $"value length must be {rules.Len}",
                value));
        }

        // min_len
        if (rules.HasMinLen && (ulong)value.Length < rules.MinLen)
        {
            violations.Add(new Violation(
                path,
                "string.min_len",
                $"value length must be at least {rules.MinLen}",
                value));
        }

        // max_len
        if (rules.HasMaxLen && (ulong)value.Length > rules.MaxLen)
        {
            violations.Add(new Violation(
                path,
                "string.max_len",
                $"value length must be at most {rules.MaxLen}",
                value));
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
                if (!SafeIsMatch(EmailRegex, value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.email",
                        "value must be a valid email address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Hostname when rules.Hostname:
                if (string.IsNullOrEmpty(value) || value.Length > 253 || !SafeIsMatch(HostnameRegex, value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.hostname",
                        "value must be a valid hostname",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ip when rules.Ip:
                if (!IPAddress.TryParse(value, out _))
                {
                    violations.Add(new Violation(
                        path,
                        "string.ip",
                        "value must be a valid IP address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ipv4 when rules.Ipv4:
                if (!IPAddress.TryParse(value, out var ipv4) || ipv4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    violations.Add(new Violation(
                        path,
                        "string.ipv4",
                        "value must be a valid IPv4 address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Ipv6 when rules.Ipv6:
                if (!IPAddress.TryParse(value, out var ipv6) || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    violations.Add(new Violation(
                        path,
                        "string.ipv6",
                        "value must be a valid IPv6 address",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.Uri when rules.Uri:
                if (!System.Uri.TryCreate(value, UriKind.Absolute, out _))
                {
                    violations.Add(new Violation(
                        path,
                        "string.uri",
                        "value must be a valid URI",
                        value));
                }
                break;

            case StringRules.WellKnownOneofCase.UriRef when rules.UriRef:
                if (!System.Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _) || string.IsNullOrEmpty(value))
                {
                    violations.Add(new Violation(
                        path,
                        "string.uri_ref",
                        "value must be a valid URI reference",
                        value));
                }
                break;
        }
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
