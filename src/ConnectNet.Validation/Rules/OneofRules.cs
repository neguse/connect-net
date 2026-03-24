using System.Collections.Generic;
using Buf.Validate;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class OneofRuleEvaluator
{
    public static void Evaluate(IMessage message, string prefix, List<Violation> violations)
    {
        // Stub - will be implemented in a later task
    }
}
