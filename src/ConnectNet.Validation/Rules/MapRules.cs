using System.Collections;
using System.Collections.Generic;
using System.Text;
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

        // keys / values — validate each key and value against their respective rules
        if (rules.Keys != null || rules.Values != null)
        {
            foreach (DictionaryEntry entry in dict)
            {
                // Attacker-controlled map keys flow into FieldPath; control characters here would
                // corrupt log lines (CR/LF injection) or terminal output (ANSI escape sequences).
                var keyStr = EscapeForPath(entry.Key?.ToString() ?? "");
                if (rules.Keys != null)
                {
                    var keyPath = $"{path}[{keyStr}].key";
                    FieldRuleEvaluator.Evaluate(rules.Keys, entry.Key, keyPath, violations);
                }
                if (rules.Values != null)
                {
                    var valuePath = $"{path}[{keyStr}]";
                    FieldRuleEvaluator.Evaluate(rules.Values, entry.Value, valuePath, violations);
                }
            }
        }
    }

    private const int MaxKeyDisplayLength = 64;

    private static string EscapeForPath(string s)
    {
        if (s.Length == 0) return s;
        StringBuilder? sb = null;
        var limit = s.Length > MaxKeyDisplayLength ? MaxKeyDisplayLength : s.Length;
        for (int i = 0; i < limit; i++)
        {
            var c = s[i];
            if (c < 0x20 || c == 0x7f || c == '"' || c == '\\' || c == '[' || c == ']')
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 8);
                sb.Append('\\');
                sb.Append('u');
                sb.Append(((int)c).ToString("x4"));
            }
            else
            {
                sb?.Append(c);
            }
        }
        if (sb == null)
        {
            return s.Length > limit ? s.Substring(0, limit) + "..." : s;
        }
        if (s.Length > limit) sb.Append("...");
        return sb.ToString();
    }
}
