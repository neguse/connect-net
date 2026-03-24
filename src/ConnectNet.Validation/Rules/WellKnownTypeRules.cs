using System.Collections.Generic;
using Buf.Validate;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Rules;

internal static class WellKnownTypeRuleEvaluator
{
    public static void EvaluateTimestamp(TimestampRules rules, Timestamp value, string path, List<Violation> violations)
    {
        // gt
        if (rules.Gt != null && value.CompareTo(rules.Gt) <= 0)
        {
            violations.Add(new Violation(
                path,
                "timestamp.gt",
                $"value must be greater than {rules.Gt}",
                value));
        }

        // gte
        if (rules.Gte != null && value.CompareTo(rules.Gte) < 0)
        {
            violations.Add(new Violation(
                path,
                "timestamp.gte",
                $"value must be greater than or equal to {rules.Gte}",
                value));
        }

        // lt
        if (rules.Lt != null && value.CompareTo(rules.Lt) >= 0)
        {
            violations.Add(new Violation(
                path,
                "timestamp.lt",
                $"value must be less than {rules.Lt}",
                value));
        }

        // lte
        if (rules.Lte != null && value.CompareTo(rules.Lte) > 0)
        {
            violations.Add(new Violation(
                path,
                "timestamp.lte",
                $"value must be less than or equal to {rules.Lte}",
                value));
        }
    }

    public static void EvaluateDuration(DurationRules rules, Duration value, string path, List<Violation> violations)
    {
        // gt
        if (rules.Gt != null && value.CompareTo(rules.Gt) <= 0)
        {
            violations.Add(new Violation(
                path,
                "duration.gt",
                $"value must be greater than {rules.Gt}",
                value));
        }

        // gte
        if (rules.Gte != null && value.CompareTo(rules.Gte) < 0)
        {
            violations.Add(new Violation(
                path,
                "duration.gte",
                $"value must be greater than or equal to {rules.Gte}",
                value));
        }

        // lt
        if (rules.Lt != null && value.CompareTo(rules.Lt) >= 0)
        {
            violations.Add(new Violation(
                path,
                "duration.lt",
                $"value must be less than {rules.Lt}",
                value));
        }

        // lte
        if (rules.Lte != null && value.CompareTo(rules.Lte) > 0)
        {
            violations.Add(new Violation(
                path,
                "duration.lte",
                $"value must be less than or equal to {rules.Lte}",
                value));
        }
    }
}
