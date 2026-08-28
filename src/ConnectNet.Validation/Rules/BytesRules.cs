using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf;

namespace ConnectNet.Validation.Rules;

internal static class BytesRuleEvaluator
{
    // Strict UTF-8 decoder: bytes.pattern applies the regex to the value interpreted as
    // UTF-8; values that are not valid UTF-8 cannot match (protovalidate/CEL semantics,
    // where string(bytes) errors on invalid UTF-8).
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Evaluate(BytesRules rules, ByteString value, string path, ViolationCollector violations)
    {
        // const
        if (rules.HasConst && value != rules.Const)
        {
            violations.Add(new Violation(
                path,
                "bytes.const",
                $"value must equal {rules.Const.ToBase64()}",
                value));
        }

        // len
        if (rules.HasLen && (ulong)value.Length != rules.Len)
        {
            violations.Add(new Violation(
                path,
                "bytes.len",
                $"value length must be {rules.Len}",
                value));
        }

        // min_len
        if (rules.HasMinLen && (ulong)value.Length < rules.MinLen)
        {
            violations.Add(new Violation(
                path,
                "bytes.min_len",
                $"value length must be at least {rules.MinLen}",
                value));
        }

        // max_len
        if (rules.HasMaxLen && (ulong)value.Length > rules.MaxLen)
        {
            violations.Add(new Violation(
                path,
                "bytes.max_len",
                $"value length must be at most {rules.MaxLen}",
                value));
        }

        // pattern
        if (rules.HasPattern)
        {
            if (!MatchesPattern(rules.Pattern, value))
            {
                violations.Add(new Violation(
                    path,
                    "bytes.pattern",
                    $"value must match pattern \"{rules.Pattern}\"",
                    value));
            }
        }

        // prefix
        if (rules.HasPrefix && !value.Span.StartsWith(rules.Prefix.Span))
        {
            violations.Add(new Violation(
                path,
                "bytes.prefix",
                $"value must have prefix {rules.Prefix.ToBase64()}",
                value));
        }

        // suffix
        if (rules.HasSuffix && !value.Span.EndsWith(rules.Suffix.Span))
        {
            violations.Add(new Violation(
                path,
                "bytes.suffix",
                $"value must have suffix {rules.Suffix.ToBase64()}",
                value));
        }

        // contains
        if (rules.HasContains && value.Span.IndexOf(rules.Contains.Span) < 0)
        {
            violations.Add(new Violation(
                path,
                "bytes.contains",
                $"value must contain {rules.Contains.ToBase64()}",
                value));
        }

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "bytes.in",
                "value must be in list",
                value));
        }

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "bytes.not_in",
                "value must not be in list",
                value));
        }
    }

    private static bool MatchesPattern(string pattern, ByteString value)
    {
        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(value.ToByteArray());
        }
        catch (DecoderFallbackException)
        {
            return false; // not valid UTF-8: cannot match
        }

        try
        {
            // Same RE2 translation as string.pattern; see Re2Pattern.
            return Re2Pattern.GetRegex(pattern).IsMatch(decoded);
        }
        catch (ArgumentException)
        {
            return false; // invalid pattern: report as violation rather than silently pass
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
