using System.Collections;
using System.Collections.Generic;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class RepeatedRuleEvaluator
{
    /// <summary>
    /// Internal cap on the number of items considered when evaluating <c>repeated.unique</c>
    /// in the absence of an explicit <c>max_items</c>. Stops a caller from pinning a worker
    /// thread (and the GC) by sending a giant repeated field of boxed value types.
    /// NOTE: this cap is a deliberate deviation from protovalidate, which evaluates
    /// <c>unique</c> over the whole list regardless of size; lists exceeding the cap are
    /// reported as a <c>repeated.unique</c> violation instead of being scanned.
    /// </summary>
    public const int DefaultUniqueScanLimit = 10_000;

    public static void Evaluate(RepeatedRules rules, object? value, string path, ViolationCollector violations,
        FieldDescriptor? fieldDescriptor = null)
    {
        if (value is not IList list)
            return;

        // min_items
        if (rules.HasMinItems && (ulong)list.Count < rules.MinItems)
        {
            violations.Add(new Violation(
                path,
                "repeated.min_items",
                $"value must contain at least {rules.MinItems} item(s)",
                list.Count));
        }

        // max_items
        if (rules.HasMaxItems && (ulong)list.Count > rules.MaxItems)
        {
            violations.Add(new Violation(
                path,
                "repeated.max_items",
                $"value must contain at most {rules.MaxItems} item(s)",
                list.Count));
        }

        // unique
        if (rules.Unique)
        {
            // Respect the explicit max_items if present; otherwise bound the scan so the
            // attacker cannot use an unbounded repeated field with `unique=true` to consume
            // O(N) heap (each element is boxed into HashSet<object>).
            var scanLimit = rules.HasMaxItems && rules.MaxItems <= int.MaxValue
                ? (int)rules.MaxItems
                : DefaultUniqueScanLimit;
            if (list.Count > scanLimit)
            {
                violations.Add(new Violation(
                    path,
                    "repeated.unique",
                    $"repeated.unique cannot be evaluated for lists larger than {scanLimit}; set max_items"));
            }
            else if (HasDuplicate(list))
            {
                violations.Add(new Violation(
                    path,
                    "repeated.unique",
                    "repeated value must contain unique items"));
            }
        }

        // items — validate each element against item-level rules. The field descriptor of
        // the repeated field itself is passed through so element-level rules that need
        // reflection info (enum.defined_only) can resolve the element type.
        if (rules.Items != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                // The element count is the caller's to choose, so stop once the collector
                // is full rather than walking a list sized by the request.
                if (violations.IsFull)
                {
                    violations.MarkTruncated();
                    break;
                }

                var itemPath = $"{path}[{i}]";
                FieldRuleEvaluator.Evaluate(rules.Items, list[i], itemPath, violations, fieldDescriptor);
            }
        }
    }

    /// <summary>
    /// Duplicate-detection that avoids boxing for the primitive element types that protobuf
    /// repeated fields use in practice. Falls back to <c>HashSet&lt;object&gt;</c> for message
    /// or enum types. Float/double NaN values are never considered duplicates of each other,
    /// matching CEL equality (NaN != NaN).
    /// </summary>
    private static bool HasDuplicate(IList list)
    {
        return list switch
        {
            IList<int> i32     => HasDup(i32),
            IList<long> i64    => HasDup(i64),
            IList<uint> u32    => HasDup(u32),
            IList<ulong> u64   => HasDup(u64),
            IList<float> f32   => HasDupFloat(f32),
            IList<double> f64  => HasDupDouble(f64),
            IList<bool> b      => HasDup(b),
            IList<string> s    => HasDup(s),
            _                  => HasDupObject(list),
        };
    }

    private static bool HasDup<T>(IList<T> list)
    {
        if (list.Count < 2) return false;
        var seen = new HashSet<T>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!seen.Add(list[i])) return true;
        }
        return false;
    }

    private static bool HasDupFloat(IList<float> list)
    {
        if (list.Count < 2) return false;
        var seen = new HashSet<float>();
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (float.IsNaN(v)) continue; // NaN != NaN
            if (!seen.Add(v)) return true;
        }
        return false;
    }

    private static bool HasDupDouble(IList<double> list)
    {
        if (list.Count < 2) return false;
        var seen = new HashSet<double>();
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (double.IsNaN(v)) continue; // NaN != NaN
            if (!seen.Add(v)) return true;
        }
        return false;
    }

    private static bool HasDupObject(IList list)
    {
        var seen = new HashSet<object>();
        foreach (var item in list)
        {
            if (item != null && !seen.Add(item)) return true;
        }
        return false;
    }
}
