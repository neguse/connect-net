using System.Collections.Generic;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class BoolRuleEvaluator
{
    public static void Evaluate(BoolRules rules, bool value, string path, List<Violation> violations)
    {
        // const
        if (rules.HasConst && value != rules.Const)
        {
            violations.Add(new Violation(
                path,
                "bool.const",
                $"value must equal {rules.Const.ToString().ToLowerInvariant()}",
                value));
        }
    }
}
