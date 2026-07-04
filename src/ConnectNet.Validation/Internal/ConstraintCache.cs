using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Buf.Validate;
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
    private readonly bool _ignoreUnsupportedRules;

    public ConstraintCache(bool ignoreUnsupportedRules = false)
    {
        _ignoreUnsupportedRules = ignoreUnsupportedRules;
    }

    public FieldConstraintInfo[] GetFieldConstraints(MessageDescriptor descriptor)
    {
        return _cache.GetValue(descriptor, BuildConstraints);
    }

    private FieldConstraintInfo[] BuildConstraints(MessageDescriptor descriptor)
    {
        if (!_ignoreUnsupportedRules)
        {
            CheckMessageRulesSupported(descriptor);
        }

        var fields = descriptor.Fields.InDeclarationOrder();
        var result = new FieldConstraintInfo[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var rules = GetFieldRules(field);
            if (rules != null && !_ignoreUnsupportedRules)
            {
                CheckFieldRulesSupported(field, rules);
            }
            result[i] = new FieldConstraintInfo(field, rules);
        }
        return result;
    }

    // Deliberately no catch-all here: if constraint extraction throws, the exception must
    // propagate (fail-closed). Swallowing it would silently drop every rule on the message.
    private static FieldRules? GetFieldRules(FieldDescriptor field)
    {
        var options = field.GetOptions();
        if (options == null)
            return null;
        return options.GetExtension(ValidateExtensions.Field);
    }

    private static void CheckMessageRulesSupported(MessageDescriptor descriptor)
    {
        var options = descriptor.GetOptions();
        if (options == null)
            return;
        var rules = options.GetExtension(ValidateExtensions.Message);
        if (rules == null)
            return;

        if (rules.Cel.Count > 0 || rules.CelExpression.Count > 0)
            throw Unsupported(descriptor.FullName, "message-level cel");
        if (rules.Oneof.Count > 0)
            throw Unsupported(descriptor.FullName, "message-level oneof");
    }

    private static void CheckFieldRulesSupported(FieldDescriptor field, FieldRules rules)
    {
        CheckFieldRulesSupported(field.FullName, rules);
    }

    private static void CheckFieldRulesSupported(string fieldName, FieldRules rules)
    {
        if (rules.Cel.Count > 0 || rules.CelExpression.Count > 0)
            throw Unsupported(fieldName, "cel");

        switch (rules.TypeCase)
        {
            case FieldRules.TypeOneofCase.Any:
                throw Unsupported(fieldName, "any");
            case FieldRules.TypeOneofCase.FieldMask:
                throw Unsupported(fieldName, "field_mask");

            case FieldRules.TypeOneofCase.String:
                switch (rules.String.WellKnownCase)
                {
                    case StringRules.WellKnownOneofCase.Address:
                        throw Unsupported(fieldName, "string.address");
                    case StringRules.WellKnownOneofCase.Ulid:
                        throw Unsupported(fieldName, "string.ulid");
                    case StringRules.WellKnownOneofCase.IpWithPrefixlen:
                        throw Unsupported(fieldName, "string.ip_with_prefixlen");
                    case StringRules.WellKnownOneofCase.Ipv4WithPrefixlen:
                        throw Unsupported(fieldName, "string.ipv4_with_prefixlen");
                    case StringRules.WellKnownOneofCase.Ipv6WithPrefixlen:
                        throw Unsupported(fieldName, "string.ipv6_with_prefixlen");
                    case StringRules.WellKnownOneofCase.IpPrefix:
                        throw Unsupported(fieldName, "string.ip_prefix");
                    case StringRules.WellKnownOneofCase.Ipv4Prefix:
                        throw Unsupported(fieldName, "string.ipv4_prefix");
                    case StringRules.WellKnownOneofCase.Ipv6Prefix:
                        throw Unsupported(fieldName, "string.ipv6_prefix");
                    case StringRules.WellKnownOneofCase.HostAndPort:
                        throw Unsupported(fieldName, "string.host_and_port");
                    case StringRules.WellKnownOneofCase.ProtobufFqn:
                        throw Unsupported(fieldName, "string.protobuf_fqn");
                    case StringRules.WellKnownOneofCase.ProtobufDotFqn:
                        throw Unsupported(fieldName, "string.protobuf_dot_fqn");
                    case StringRules.WellKnownOneofCase.WellKnownRegex:
                        throw Unsupported(fieldName, "string.well_known_regex");
                }
                break;

            case FieldRules.TypeOneofCase.Bytes:
                switch (rules.Bytes.WellKnownCase)
                {
                    case BytesRules.WellKnownOneofCase.Ip:
                        throw Unsupported(fieldName, "bytes.ip");
                    case BytesRules.WellKnownOneofCase.Ipv4:
                        throw Unsupported(fieldName, "bytes.ipv4");
                    case BytesRules.WellKnownOneofCase.Ipv6:
                        throw Unsupported(fieldName, "bytes.ipv6");
                    case BytesRules.WellKnownOneofCase.Uuid:
                        throw Unsupported(fieldName, "bytes.uuid");
                }
                break;

            case FieldRules.TypeOneofCase.Repeated:
                if (rules.Repeated.Items != null)
                    CheckFieldRulesSupported(fieldName, rules.Repeated.Items);
                break;

            case FieldRules.TypeOneofCase.Map:
                if (rules.Map.Keys != null)
                    CheckFieldRulesSupported(fieldName, rules.Map.Keys);
                if (rules.Map.Values != null)
                    CheckFieldRulesSupported(fieldName, rules.Map.Values);
                break;
        }
    }

    private static NotSupportedException Unsupported(string subject, string ruleName)
    {
        return new NotSupportedException(
            $"'{subject}' uses the '{ruleName}' rule, which is not supported by ConnectNet.Validation. " +
            "Validation refuses to run rather than silently pass. " +
            "Construct the ProtoValidator with ignoreUnsupportedRules=true to skip unsupported rules.");
    }
}
