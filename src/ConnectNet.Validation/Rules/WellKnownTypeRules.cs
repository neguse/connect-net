using System;
using System.Collections.Generic;
using System.Linq;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Rules;

internal static class WellKnownTypeRuleEvaluator
{
    public static void EvaluateTimestamp(TimestampRules rules, Timestamp value, string path, ViolationCollector violations)
    {
        // const
        if (rules.Const != null && !value.Equals(rules.Const))
        {
            violations.Add(new Violation(
                path,
                "timestamp.const",
                $"value must equal {rules.Const}",
                value));
        }

        EvaluateRange(
            rules.Gt, rules.Gte, rules.Lt, rules.Lte,
            value, path, "timestamp", violations,
            static (a, b) => a.CompareTo(b));

        // lt_now / gt_now / within evaluate against the current time.
        if (rules.LtNow || rules.GtNow || rules.Within != null)
        {
            var now = DateTimeOffset.UtcNow;
            var dto = value.ToDateTimeOffset();

            if (rules.LtNow && dto > now)
            {
                violations.Add(new Violation(
                    path,
                    "timestamp.lt_now",
                    "value must be less than now",
                    value));
            }

            if (rules.GtNow && dto < now)
            {
                violations.Add(new Violation(
                    path,
                    "timestamp.gt_now",
                    "value must be greater than now",
                    value));
            }

            if (rules.Within != null)
            {
                var within = rules.Within.ToTimeSpan();
                if (dto < now - within || dto > now + within)
                {
                    violations.Add(new Violation(
                        path,
                        "timestamp.within",
                        $"value must be within {rules.Within} of now",
                        value));
                }
            }
        }
    }

    public static void EvaluateDuration(DurationRules rules, Duration value, string path, ViolationCollector violations)
    {
        // const
        if (rules.Const != null && !value.Equals(rules.Const))
        {
            violations.Add(new Violation(
                path,
                "duration.const",
                $"value must equal {rules.Const}",
                value));
        }

        EvaluateRange(
            rules.Gt, rules.Gte, rules.Lt, rules.Lte,
            value, path, "duration", violations,
            static (a, b) => a.CompareTo(b));

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "duration.in",
                $"value must be in list [{string.Join(", ", rules.In)}]",
                value));
        }

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "duration.not_in",
                $"value must not be in list [{string.Join(", ", rules.NotIn)}]",
                value));
        }
    }

    /// <summary>
    /// Range evaluation with the same reversed-range (OR) semantics as the numeric rules:
    /// when the upper bound (lt/lte) is below the lower bound (gt/gte), the value must fall
    /// outside the band. Mirrors the CEL rules in buf/validate/validate.proto.
    /// </summary>
    private static void EvaluateRange<T>(
        T? gt, T? gte, T? lt, T? lte,
        T value, string path, string typeName,
        ViolationCollector violations,
        Comparison<T> compare) where T : class
    {
        var lower = gt ?? gte;
        var upper = lt ?? lte;

        if (lower != null && upper != null)
        {
            var lowerId = gt != null ? "gt" : "gte";
            var upperId = lt != null ? "lt" : "lte";
            var lowerDesc = gt != null ? "greater than" : "greater than or equal to";
            var upperDesc = lt != null ? "less than" : "less than or equal to";

            if (compare(upper, lower) >= 0)
            {
                bool belowLower = gt != null ? compare(value, gt) <= 0 : compare(value, gte!) < 0;
                bool aboveUpper = lt != null ? compare(value, lt) >= 0 : compare(value, lte!) > 0;
                if (belowLower || aboveUpper)
                {
                    violations.Add(new Violation(
                        path,
                        $"{typeName}.{lowerId}_{upperId}",
                        $"value must be {lowerDesc} {lower} and {upperDesc} {upper}",
                        value));
                }
            }
            else
            {
                bool aboveLower = gt != null ? compare(value, gt) > 0 : compare(value, gte!) >= 0;
                bool belowUpper = lt != null ? compare(value, lt) < 0 : compare(value, lte!) <= 0;
                if (!aboveLower && !belowUpper)
                {
                    violations.Add(new Violation(
                        path,
                        $"{typeName}.{lowerId}_{upperId}_exclusive",
                        $"value must be {lowerDesc} {lower} or {upperDesc} {upper}",
                        value));
                }
            }
            return;
        }

        if (gt != null && compare(value, gt) <= 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gt",
                $"value must be greater than {gt}",
                value));
        }

        if (gte != null && compare(value, gte) < 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gte",
                $"value must be greater than or equal to {gte}",
                value));
        }

        if (lt != null && compare(value, lt) >= 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lt",
                $"value must be less than {lt}",
                value));
        }

        if (lte != null && compare(value, lte) > 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lte",
                $"value must be less than or equal to {lte}",
                value));
        }
    }
}
