using System.Collections;
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

    private readonly ConstraintCache _cache;

    public ProtoValidator() : this(ignoreUnsupportedRules: false)
    {
    }

    /// <param name="ignoreUnsupportedRules">
    /// When false (default), encountering a protovalidate rule this implementation does not
    /// support (CEL rules, some well-known string formats, AnyRules, FieldMaskRules, ...)
    /// throws <see cref="System.NotSupportedException"/> instead of silently passing.
    /// Set to true to skip such rules.
    /// </param>
    public ProtoValidator(bool ignoreUnsupportedRules)
    {
        IgnoreUnsupportedRules = ignoreUnsupportedRules;
        _cache = new ConstraintCache(ignoreUnsupportedRules);
    }

    /// <summary>
    /// Whether rules not supported by this implementation are silently skipped instead of
    /// causing <see cref="System.NotSupportedException"/>. Defaults to false (fail loud).
    /// </summary>
    public bool IgnoreUnsupportedRules { get; }

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

            if (rules != null && rules.Ignore == Ignore.Always)
                continue; // skip all rules, including required and nested validation

            var path = string.IsNullOrEmpty(prefix)
                ? field.JsonName
                : $"{prefix}.{field.JsonName}";

            var accessor = field.Accessor;

            // Presence semantics (IGNORE_UNSPECIFIED default behavior): fields that track
            // presence (proto3 optional, oneof members, message fields) are only validated
            // when set. `required` demands that they are set.
            if (field.HasPresence)
            {
                if (!accessor.HasValue(message))
                {
                    if (rules != null && rules.Required)
                    {
                        violations.Add(new Violation(path, "required", "value is required"));
                    }
                    continue; // unset: ignore all other rules, nothing to recurse into
                }
            }

            var value = accessor.GetValue(message);
            var isZero = IsDefaultValue(field, value);

            if (!field.HasPresence)
            {
                // IGNORE_IF_ZERO_VALUE only affects fields without presence tracking
                // (for presence-tracking fields it is a no-op per buf.validate.Ignore docs).
                if (rules != null && rules.Ignore == Ignore.IfZeroValue && isZero)
                    continue;

                // `required` on an implicit-presence field means "must not be the zero value".
                if (rules != null && rules.Required && isZero)
                {
                    violations.Add(new Violation(path, "required", "value is required"));
                }
            }

            // Evaluate type-specific rules via FieldRuleEvaluator
            if (rules != null && value != null)
            {
                FieldRuleEvaluator.Evaluate(rules, value, path, violations, field);
            }

            // Recurse into nested messages (singular, repeated elements, and map values).
            if (field.FieldType != FieldType.Message || value == null)
                continue;

            if (field.IsMap)
            {
                var valueField = field.MessageType.FindFieldByNumber(2);
                if (valueField.FieldType == FieldType.Message && value is IDictionary dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Value is IMessage entryMessage)
                        {
                            var entryPath = path + FieldPaths.MapKeySubscript(entry.Key);
                            ValidateMessage(entryMessage, entryPath, violations, depth + 1);
                        }
                    }
                }
            }
            else if (field.IsRepeated)
            {
                if (value is IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is IMessage itemMessage)
                        {
                            ValidateMessage(itemMessage, $"{path}[{i}]", violations, depth + 1);
                        }
                    }
                }
            }
            else if (value is IMessage nestedMessage)
            {
                ValidateMessage(nestedMessage, path, violations, depth + 1);
            }
        }

        // Evaluate oneof constraints
        OneofRuleEvaluator.Evaluate(message, prefix, violations);
    }

    private static bool IsDefaultValue(FieldDescriptor field, object? value)
    {
        if (value == null)
            return true;

        if (field.IsMap)
            return value is IDictionary dict && dict.Count == 0;

        if (field.IsRepeated)
            return value is IList list && list.Count == 0;

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
