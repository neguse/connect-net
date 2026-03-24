using System.Collections;
using System.Collections.Generic;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class RepeatedRuleEvaluator
{
    public static void Evaluate(RepeatedRules rules, object? value, string path, List<Violation> violations)
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
            var seen = new HashSet<object>();
            bool hasDuplicate = false;
            foreach (var item in list)
            {
                if (item != null && !seen.Add(item))
                {
                    hasDuplicate = true;
                    break;
                }
            }

            if (hasDuplicate)
            {
                violations.Add(new Violation(
                    path,
                    "repeated.unique",
                    "repeated value must contain unique items"));
            }
        }

        // items — validate each element against item-level rules
        if (rules.Items != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var itemPath = $"{path}[{i}]";
                FieldRuleEvaluator.Evaluate(rules.Items, list[i], itemPath, violations);
            }
        }
    }
}
