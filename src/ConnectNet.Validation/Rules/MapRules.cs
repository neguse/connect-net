using System.Collections;
using System.Collections.Generic;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class MapRuleEvaluator
{
    public static void Evaluate(MapRules rules, object? value, string path, List<Violation> violations)
    {
        if (value is not IDictionary dict)
            return;

        // min_pairs
        if (rules.HasMinPairs && (ulong)dict.Count < rules.MinPairs)
        {
            violations.Add(new Violation(
                path,
                "map.min_pairs",
                $"map must contain at least {rules.MinPairs} entry/entries",
                dict.Count));
        }

        // max_pairs
        if (rules.HasMaxPairs && (ulong)dict.Count > rules.MaxPairs)
        {
            violations.Add(new Violation(
                path,
                "map.max_pairs",
                $"map must contain at most {rules.MaxPairs} entry/entries",
                dict.Count));
        }
    }
}
