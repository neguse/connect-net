using System.Collections;
using System.Collections.Generic;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class MapRuleEvaluator
{
    public static void Evaluate(MapRules rules, object? value, string path, List<Violation> violations,
        FieldDescriptor? fieldDescriptor = null)
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

        // keys / values — validate each key and value against their respective rules.
        // The synthetic map-entry descriptor provides the key (1) and value (2) field
        // descriptors, needed by rules like enum.defined_only.
        if (rules.Keys != null || rules.Values != null)
        {
            var keyField = fieldDescriptor?.MessageType?.FindFieldByNumber(1);
            var valueField = fieldDescriptor?.MessageType?.FindFieldByNumber(2);

            foreach (DictionaryEntry entry in dict)
            {
                var entryPath = path + FieldPaths.MapKeySubscript(entry.Key);
                if (rules.Keys != null)
                {
                    // Key violations share the entry path; they are distinguished from value
                    // violations by Violation.ForKey (protovalidate's for_key flag).
                    var before = violations.Count;
                    FieldRuleEvaluator.Evaluate(rules.Keys, entry.Key, entryPath, violations, keyField);
                    for (int i = before; i < violations.Count; i++)
                    {
                        violations[i].ForKey = true;
                    }
                }
                if (rules.Values != null)
                {
                    FieldRuleEvaluator.Evaluate(rules.Values, entry.Value, entryPath, violations, valueField);
                }
            }
        }
    }
}
