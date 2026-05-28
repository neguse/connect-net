using System.Collections.Generic;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using ConnectNet.Validation.Rules;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation;

public class ProtoValidator
{
    /// <summary>
    /// Hard cap on message nesting depth during validation. A recursive proto crafted to
    /// exceed this depth would cause StackOverflowException (which is uncatchable in .NET);
    /// rejecting early surfaces it as a normal validation result instead.
    /// </summary>
    public const int MaxRecursionDepth = 32;

    private readonly ConstraintCache _cache = new();

    public ValidationResult Validate(IMessage message)
    {
        var violations = new List<Violation>();
        ValidateMessage(message, "", violations, depth: 0);
        return violations.Count == 0 ? ValidationResult.Success : ValidationResult.Fail(violations);
    }

    private void ValidateMessage(IMessage message, string prefix, List<Violation> violations, int depth)
    {
        if (depth >= MaxRecursionDepth)
        {
            violations.Add(new Violation(prefix, "recursion_limit",
                $"message nesting exceeds validation recursion limit ({MaxRecursionDepth})"));
            return;
        }

        var constraints = _cache.GetFieldConstraints(message.Descriptor);

        foreach (var constraint in constraints)
        {
            var field = constraint.Field;
            var rules = constraint.Rules;

            if (rules == null)
                continue;

            if (ShouldIgnore(rules, field, message))
                continue;

            var path = string.IsNullOrEmpty(prefix)
                ? field.JsonName
                : $"{prefix}.{field.JsonName}";

            var accessor = field.Accessor;
            var value = accessor.GetValue(message);

            // Check required for message-type fields
            if (rules.Required && field.FieldType == FieldType.Message && value == null)
            {
                violations.Add(new Violation(path, "required", "value is required"));
                continue;
            }

            // Evaluate type-specific rules via FieldRuleEvaluator
            if (value != null)
            {
                FieldRuleEvaluator.Evaluate(rules, value, path, violations, field);
            }

            // Recurse into nested messages
            if (field.FieldType == FieldType.Message
                && !field.IsRepeated
                && !field.IsMap
                && value is IMessage nestedMessage)
            {
                ValidateMessage(nestedMessage, path, violations, depth + 1);
            }
        }

        // Evaluate oneof constraints
        OneofRuleEvaluator.Evaluate(message, prefix, violations);
    }

    private static bool ShouldIgnore(FieldRules rules, FieldDescriptor field, IMessage message)
    {
        if (rules.Ignore == Ignore.Always)
            return true;

        if (rules.Ignore == Ignore.IfZeroValue)
        {
            var value = field.Accessor.GetValue(message);
            if (IsDefaultValue(field, value))
                return true;
        }

        return false;
    }

    private static bool IsDefaultValue(FieldDescriptor field, object? value)
    {
        if (value == null)
            return true;

        return field.FieldType switch
        {
            FieldType.String => value is string s && s.Length == 0,
            FieldType.Bytes => value is ByteString bs && bs.IsEmpty,
            FieldType.Bool => value is bool b && !b,
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => value is int i && i == 0,
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => value is long l && l == 0,
            FieldType.UInt32 or FieldType.Fixed32 => value is uint u && u == 0,
            FieldType.UInt64 or FieldType.Fixed64 => value is ulong ul && ul == 0,
            FieldType.Float => value is float f && f == 0f,
            FieldType.Double => value is double d && d == 0d,
            FieldType.Enum => value is System.Enum enumVal && System.Convert.ToInt32(enumVal) == 0,
            FieldType.Message => false, // message presence is handled separately
            _ => false,
        };
    }
}
