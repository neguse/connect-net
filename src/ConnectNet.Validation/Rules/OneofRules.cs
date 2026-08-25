using System.Collections.Generic;
using Buf.Validate;
using ConnectNet.Validation.Internal;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class OneofRuleEvaluator
{
    public static void Evaluate(IMessage message, string prefix, ViolationCollector violations)
    {
        var descriptor = message.Descriptor;

        foreach (var oneofDescriptor in descriptor.Oneofs)
        {
            // Skip synthetic oneofs (proto3 optional fields)
            if (oneofDescriptor.IsSynthetic)
                continue;

            var options = oneofDescriptor.GetOptions();
            if (options == null)
                continue;

            var rules = options.GetExtension(ValidateExtensions.Oneof);
            if (rules == null)
                continue;

            if (rules.Required)
            {
                var caseField = oneofDescriptor.Accessor.GetCaseFieldDescriptor(message);
                if (caseField == null)
                {
                    var path = string.IsNullOrEmpty(prefix)
                        ? oneofDescriptor.Name
                        : $"{prefix}.{oneofDescriptor.Name}";

                    violations.Add(new Violation(
                        path,
                        "oneof.required",
                        $"exactly one field is required in oneof '{oneofDescriptor.Name}'"));
                }
            }
        }
    }
}
