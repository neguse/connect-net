using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Buf.Validate;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Internal;

internal sealed class FieldConstraintInfo
{
    public FieldDescriptor Field { get; }
    public FieldRules? Rules { get; }

    public FieldConstraintInfo(FieldDescriptor field, FieldRules? rules)
    {
        Field = field;
        Rules = rules;
    }
}

internal sealed class ConstraintCache
{
    // Keyed by the MessageDescriptor instance (via ConditionalWeakTable) so that:
    //   * descriptors from different assemblies/versions that happen to share FullName
    //     don't poison each other's FieldDescriptor entries
    //   * entries are reclaimed when their descriptors are GCed, avoiding monotonic memory
    //     growth for dynamically loaded protos.
    private readonly ConditionalWeakTable<MessageDescriptor, FieldConstraintInfo[]> _cache = new();

    public FieldConstraintInfo[] GetFieldConstraints(MessageDescriptor descriptor)
    {
        return _cache.GetValue(descriptor, BuildConstraints);
    }

    private static FieldConstraintInfo[] BuildConstraints(MessageDescriptor descriptor)
    {
        var result = new List<FieldConstraintInfo>();
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            var rules = GetFieldRules(field);
            result.Add(new FieldConstraintInfo(field, rules));
        }
        return result.ToArray();
    }

    private static FieldRules? GetFieldRules(FieldDescriptor field)
    {
        try
        {
            var options = field.GetOptions();
            if (options == null)
                return null;
            return options.GetExtension(ValidateExtensions.Field);
        }
        catch
        {
            return null;
        }
    }
}
