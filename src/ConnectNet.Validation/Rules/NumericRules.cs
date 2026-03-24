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
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "float", violations);

    public static void EvaluateDouble(DoubleRules rules, double value, string path, List<Violation> violations)
        => Evaluate(rules.HasConst, rules.Const, rules.HasGt, rules.Gt, rules.HasGte, rules.Gte,
            rules.HasLt, rules.Lt, rules.HasLte, rules.Lte, rules.In, rules.NotIn,
            value, path, "double", violations);

    private static void Evaluate<T>(
        bool hasConst, T constVal,
        bool hasGt, T gt, bool hasGte, T gte,
        bool hasLt, T lt, bool hasLte, T lte,
        IEnumerable<T> inList, IEnumerable<T> notInList,
        T value, string path, string typeName,
        List<Violation> violations) where T : IComparable<T>
    {
        // const
        if (hasConst && value.CompareTo(constVal) != 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.const",
                $"value must equal {constVal}",
                value));
        }

        // gt
        if (hasGt && value.CompareTo(gt) <= 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gt",
                $"value must be greater than {gt}",
                value));
        }

        // gte
        if (hasGte && value.CompareTo(gte) < 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.gte",
                $"value must be greater than or equal to {gte}",
                value));
        }

        // lt
        if (hasLt && value.CompareTo(lt) >= 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lt",
                $"value must be less than {lt}",
                value));
        }

        // lte
        if (hasLte && value.CompareTo(lte) > 0)
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.lte",
                $"value must be less than or equal to {lte}",
                value));
        }

        // in
        var inCollection = inList as ICollection<T> ?? inList.ToList();
        if (inCollection.Count > 0 && !inCollection.Contains(value))
        {
            violations.Add(new Violation(
                path,
                $"{typeName}.in",
                $"value must be in list [{string.Join(", ", inCollection)}]",
                value));
        }

        // not_in
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
