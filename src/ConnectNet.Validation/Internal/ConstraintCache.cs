using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    private readonly ConcurrentDictionary<string, FieldConstraintInfo[]> _cache = new();

    public FieldConstraintInfo[] GetFieldConstraints(MessageDescriptor descriptor)
    {
        return _cache.GetOrAdd(descriptor.FullName, _ => BuildConstraints(descriptor));
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
