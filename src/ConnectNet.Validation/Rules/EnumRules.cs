using System.Collections.Generic;
using System.Linq;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class EnumRuleEvaluator
{
    public static void Evaluate(EnumRules rules, int value, string path, FieldDescriptor? fieldDescriptor, ViolationCollector violations)
    {
        // const
        if (rules.HasConst && value != rules.Const)
        {
            violations.Add(new Violation(
                path,
                "enum.const",
                $"must equal {rules.Const}",
                value));
        }

        // defined_only
        if (rules.DefinedOnly && fieldDescriptor?.EnumType != null)
        {
            var enumDescriptor = fieldDescriptor.EnumType;
            var defined = enumDescriptor.Values.Any(v => v.Number == value);
            if (!defined)
            {
                violations.Add(new Violation(
                    path,
                    "enum.defined_only",
                    $"value must be one of the defined enum values",
                    value));
            }
        }

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "enum.in",
                $"value must be in list [{string.Join(", ", rules.In)}]",
                value));
        }

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "enum.not_in",
                $"value must not be in list [{string.Join(", ", rules.NotIn)}]",
                value));
        }
    }
}
