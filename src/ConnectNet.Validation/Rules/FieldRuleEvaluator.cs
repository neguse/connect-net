using System.Collections.Generic;
using Buf.Validate;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Rules;

internal static class FieldRuleEvaluator
{
    public static void Evaluate(FieldRules rules, object? value, string path, List<Violation> violations,
        FieldDescriptor? fieldDescriptor = null)
    {
        switch (rules.TypeCase)
        {
            case FieldRules.TypeOneofCase.String:
                if (value is string strValue)
                {
                    StringRuleEvaluator.Evaluate(rules.String, strValue, path, violations);
                }
                break;

            case FieldRules.TypeOneofCase.Int32:
                if (value is int int32Value)
                    NumericRuleEvaluator.EvaluateInt32(rules.Int32, int32Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Int64:
                if (value is long int64Value)
                    NumericRuleEvaluator.EvaluateInt64(rules.Int64, int64Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Uint32:
                if (value is uint uint32Value)
                    NumericRuleEvaluator.EvaluateUInt32(rules.Uint32, uint32Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Uint64:
                if (value is ulong uint64Value)
                    NumericRuleEvaluator.EvaluateUInt64(rules.Uint64, uint64Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Sint32:
                if (value is int sint32Value)
                    NumericRuleEvaluator.EvaluateSInt32(rules.Sint32, sint32Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Sint64:
                if (value is long sint64Value)
                    NumericRuleEvaluator.EvaluateSInt64(rules.Sint64, sint64Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Fixed32:
                if (value is uint fixed32Value)
                    NumericRuleEvaluator.EvaluateFixed32(rules.Fixed32, fixed32Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Fixed64:
                if (value is ulong fixed64Value)
                    NumericRuleEvaluator.EvaluateFixed64(rules.Fixed64, fixed64Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Sfixed32:
                if (value is int sfixed32Value)
                    NumericRuleEvaluator.EvaluateSFixed32(rules.Sfixed32, sfixed32Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Sfixed64:
                if (value is long sfixed64Value)
                    NumericRuleEvaluator.EvaluateSFixed64(rules.Sfixed64, sfixed64Value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Float:
                if (value is float floatValue)
                    NumericRuleEvaluator.EvaluateFloat(rules.Float, floatValue, path, violations);
                break;

            case FieldRules.TypeOneofCase.Double:
                if (value is double doubleValue)
                    NumericRuleEvaluator.EvaluateDouble(rules.Double, doubleValue, path, violations);
                break;

            case FieldRules.TypeOneofCase.Bool:
                if (value is bool boolValue)
                    BoolRuleEvaluator.Evaluate(rules.Bool, boolValue, path, violations);
                break;

            case FieldRules.TypeOneofCase.Bytes:
                if (value is ByteString bytesValue)
                    BytesRuleEvaluator.Evaluate(rules.Bytes, bytesValue, path, violations);
                break;

            case FieldRules.TypeOneofCase.Enum:
                if (value is System.Enum enumObj)
                    EnumRuleEvaluator.Evaluate(rules.Enum, System.Convert.ToInt32(enumObj), path, fieldDescriptor, violations);
                else if (value is int enumValue)
                    EnumRuleEvaluator.Evaluate(rules.Enum, enumValue, path, fieldDescriptor, violations);
                break;

            case FieldRules.TypeOneofCase.Repeated:
                RepeatedRuleEvaluator.Evaluate(rules.Repeated, value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Map:
                MapRuleEvaluator.Evaluate(rules.Map, value, path, violations);
                break;

            case FieldRules.TypeOneofCase.Timestamp:
                if (value is Timestamp timestampValue)
                    WellKnownTypeRuleEvaluator.EvaluateTimestamp(rules.Timestamp, timestampValue, path, violations);
                break;

            case FieldRules.TypeOneofCase.Duration:
                if (value is Duration durationValue)
                    WellKnownTypeRuleEvaluator.EvaluateDuration(rules.Duration, durationValue, path, violations);
                break;

            default:
                // Other type rules not yet implemented
                break;
        }
    }
}
