using System.Collections.Generic;
using System.Linq;
using Buf.Validate;
using Google.Protobuf;

namespace ConnectNet.Validation.Rules;

internal static class BytesRuleEvaluator
{
    public static void Evaluate(BytesRules rules, ByteString value, string path, List<Violation> violations)
    {
        // const
        if (rules.HasConst && value != rules.Const)
        {
            violations.Add(new Violation(
                path,
                "bytes.const",
                $"value must equal {rules.Const.ToBase64()}",
                value));
        }

        // len
        if (rules.HasLen && (ulong)value.Length != rules.Len)
        {
            violations.Add(new Violation(
                path,
                "bytes.len",
                $"value length must be {rules.Len}",
                value));
        }

        // min_len
        if (rules.HasMinLen && (ulong)value.Length < rules.MinLen)
        {
            violations.Add(new Violation(
                path,
                "bytes.min_len",
                $"value length must be at least {rules.MinLen}",
                value));
        }

        // max_len
        if (rules.HasMaxLen && (ulong)value.Length > rules.MaxLen)
        {
            violations.Add(new Violation(
                path,
                "bytes.max_len",
                $"value length must be at most {rules.MaxLen}",
                value));
        }

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "bytes.in",
                "value must be in list",
                value));
        }

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
        {
            violations.Add(new Violation(
                path,
                "bytes.not_in",
                "value must not be in list",
                value));
        }
    }
}
