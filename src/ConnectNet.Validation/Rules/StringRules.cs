using System.Collections.Generic;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class StringRuleEvaluator
{
    public static void Evaluate(StringRules rules, string value, string path, List<Violation> violations)
    {
        if (rules.HasMinLen && (ulong)value.Length < rules.MinLen)
        {
            violations.Add(new Violation(
                path,
                "string.min_len",
                $"value length must be at least {rules.MinLen}",
                value));
        }
    }
}
