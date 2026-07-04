using System;
using System.Collections.Generic;
using System.Linq;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class NumericRuleEvaluator
{
    public static void EvaluateInt32(Int32Rules rules, int value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "int32", violations);

    public static void EvaluateInt64(Int64Rules rules, long value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "int64", violations);

    public static void EvaluateUInt32(UInt32Rules rules, uint value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "uint32", violations);

    public static void EvaluateUInt64(UInt64Rules rules, ulong value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "uint64", violations);

    public static void EvaluateSInt32(SInt32Rules rules, int value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "sint32", violations);

    public static void EvaluateSInt64(SInt64Rules rules, long value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "sint64", violations);

    public static void EvaluateFixed32(Fixed32Rules rules, uint value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "fixed32", violations);

    public static void EvaluateFixed64(Fixed64Rules rules, ulong value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "fixed64", violations);

    public static void EvaluateSFixed32(SFixed32Rules rules, int value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "sfixed32", violations);

    public static void EvaluateSFixed64(SFixed64Rules rules, long value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "sfixed64", violations);

    public static void EvaluateFloat(FloatRules rules, float value, string path, List<Violation> violations)
    {
        if (rules.HasFinite && rules.Finite && (float.IsNaN(value) || float.IsInfinity(value)))
        {
            violations.Add(new Violation(path, "float.finite", "value must be finite", value));
        }

        Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "float", violations, isNaN: float.IsNaN(value));
    }

    public static void EvaluateDouble(DoubleRules rules, double value, string path, List<Violation> violations)
    {
        if (rules.HasFinite && rules.Finite && (double.IsNaN(value) || double.IsInfinity(value)))
        {
            violations.Add(new Violation(path, "double.finite", "value must be finite", value));
        }

        Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "double", violations, isNaN: double.IsNaN(value));
    }

    /// <summary>
    /// Shared comparison logic implementing protovalidate semantics:
    /// <list type="bullet">
    /// <item>when both a lower (gt/gte) and an upper (lt/lte) bound are present and the upper
    /// bound is below the lower bound, the range is reversed and the value must fall outside
    /// the band (OR semantics), mirroring the CEL rules in buf/validate/validate.proto;</item>
    /// <item>NaN always violates const/in and every range rule (CEL treats NaN as unordered
    /// and never equal to anything, including itself).</item>
    /// </list>
    /// </summary>
    private static void Evaluate<T>(
        bool hasConst, T constVal,
        bool hasGt, T gt, bool hasGte, T gte,
        bool hasLt, T lt, bool hasLte, T lte,
        IEnumerable<T> inList, IEnumerable<T> notInList,
        T value, string path, string typeName,
        List<Violation> violations,
        bool isNaN = false) where T : IComparable<T>
    {
        // const — NaN never equals anything (including a NaN const)
        if (hasConst && (isNaN || value.CompareTo(constVal) != 0))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.const",
                $"value must equal {constVal}",
                value));
        }

        EvaluateRange(hasGt, gt, hasGte, gte, hasLt, lt, hasLte, lte, value, path, typeName, violations, isNaN);

        // in — NaN is never a member of the list
        var inCollection = inList as ICollection<T> ?? inList.ToList();
        if (inCollection.Count > 0 && (isNaN || !inCollection.Contains(value)))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.in",
                $"value must be in list [{string.Join(", ", inCollection)}]",
                value));
        }

        // not_in — NaN is never a member of the list, so it always passes
        if (!isNaN)
        {
            var notInCollection = notInList as ICollection<T> ?? notInList.ToList();
            if (notInCollection.Count > 0 && notInCollection.Contains(value))
            {
                violations.Add(new Violation(
                    path,
                    $"{typeName}.not_in",
                    $"value must not be in list [{string.Join(", ", notInCollection)}]",
                    value));
            }
        }
    }

    private static void EvaluateRange<T>(
        bool hasGt, T gt, bool hasGte, T gte,
        bool hasLt, T lt, bool hasLte, T lte,
        T value, string path, string typeName,
        List<Violation> violations,
        bool isNaN) where T : IComparable<T>
    {
        bool hasLower = hasGt || hasGte;
        bool hasUpper = hasLt || hasLte;

        if (!hasLower && !hasUpper)
            return;

        if (hasLower && hasUpper)
        {
            var lower = hasGt ? gt : gte;
            var upper = hasLt ? lt : lte;
            var lowerId = hasGt ? "gt" : "gte";
            var upperId = hasLt ? "lt" : "lte";
            var lowerDesc = hasGt ? "greater than" : "greater than or equal to";
            var upperDesc = hasLt ? "less than" : "less than or equal to";

            if (upper.CompareTo(lower) >= 0)
            {
                // Standard range: lower AND upper must both hold.
                bool belowLower = hasGt ? value.CompareTo(gt) <= 0 : value.CompareTo(gte) < 0;
                bool aboveUpper = hasLt ? value.CompareTo(lt) >= 0 : value.CompareTo(lte) > 0;
                if (isNaN || belowLower || aboveUpper)
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
                // Reversed range: value must be above the lower bound OR below the upper bound.
                bool aboveLower = hasGt ? value.CompareTo(gt) > 0 : value.CompareTo(gte) >= 0;
                bool belowUpper = hasLt ? value.CompareTo(lt) < 0 : value.CompareTo(lte) <= 0;
                if (isNaN || (!aboveLower && !belowUpper))
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

        // Single-bound rules.
        if (hasGt && (isNaN || value.CompareTo(gt) <= 0))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gt",
                $"value must be greater than {gt}",
                value));
        }

        if (hasGte && (isNaN || value.CompareTo(gte) < 0))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gte",
                $"value must be greater than or equal to {gte}",
                value));
        }

        if (hasLt && (isNaN || value.CompareTo(lt) >= 0))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lt",
                $"value must be less than {lt}",
                value));
        }

        if (hasLte && (isNaN || value.CompareTo(lte) > 0))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lte",
                $"value must be less than or equal to {lte}",
                value));
        }
    }
}
