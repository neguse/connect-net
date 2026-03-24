using System.Collections.Generic;
using Buf.Validate;

namespace ConnectNet.Validation.Rules;

internal static class FieldRuleEvaluator
{
    public static void Evaluate(FieldRules rules, object? value, string path, List<Violation> violations)
    {
        switch (rules.TypeCase)
        {
            case FieldRules.TypeOneofCase.String:
                if (value is string strValue)
                {
                    StringRuleEvaluator.Evaluate(rules.String, strValue, path, violations);
                }
                break;

            case FieldRules.TypeOneofCase.Repeated:
                RepeatedRuleEvaluator.Evaluate(rules.Repeated, value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Map:
                MapRuleEvaluator.Evaluate(rules.Map, value, path, violations);
                break;

            default:
                // Other type rules not yet implemented
                break;
        }
    }
}
