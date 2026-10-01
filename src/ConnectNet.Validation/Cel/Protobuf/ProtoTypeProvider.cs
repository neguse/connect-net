using System;
using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Cel.Protobuf;

/// <summary>How protobuf enums appear to CEL.</summary>
internal enum EnumMode
{
    /// <summary>Enums are ints, as the specification's default semantics define.</summary>
    Legacy,

    /// <summary>Enums are distinct types with named constants and conversion functions.</summary>
    Strong,
}

/// <summary>
/// The type provider and message factory for a set of protobuf files: message and enum
/// descriptors (transitively through imports), extensions by full name, and the registries
/// that unpack <c>google.protobuf.Any</c>. Instances are immutable and shareable.
/// </summary>
internal sealed class ProtoTypeProvider : TypeProvider, IMessageFactoryProvider
{
    private readonly Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptor> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (EnumDescriptor Enum, int Number)> _enumValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldDescriptor> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FieldDescriptor>> _extensionsByExtendee = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IMessageFactory> _factories = new(StringComparer.Ordinal);

    public ProtoTypeProvider(IEnumerable<FileDescriptor> files, EnumMode enumMode = EnumMode.Legacy)
    {
        EnumMode = enumMode;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<FileDescriptor>(files);
        var registryFiles = new List<FileDescriptor>();
        var extensionRegistry = new ExtensionRegistry();
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file.Name)) continue;
            registryFiles.Add(file);
            foreach (var dep in file.Dependencies) queue.Enqueue(dep);
            foreach (var msg in file.MessageTypes) Register(msg, extensionRegistry);
            foreach (var en in file.EnumTypes) Register(en);
            foreach (var ext in file.Extensions.UnorderedExtensions) RegisterExtension(ext, extensionRegistry);
        }
        TypeRegistry = TypeRegistry.FromFiles(registryFiles);
        ExtensionRegistry = extensionRegistry;
    }

    public EnumMode EnumMode { get; }

    public TypeRegistry TypeRegistry { get; }

    public ExtensionRegistry ExtensionRegistry { get; }

    private void Register(MessageDescriptor msg, ExtensionRegistry extensionRegistry)
    {
        _messages[msg.FullName] = msg;
        foreach (var nested in msg.NestedTypes) Register(nested, extensionRegistry);
        foreach (var en in msg.EnumTypes) Register(en);
        foreach (var ext in msg.Extensions.UnorderedExtensions) RegisterExtension(ext, extensionRegistry);
    }

    private void Register(EnumDescriptor en)
    {
        _enums[en.FullName] = en;
        foreach (var value in en.Values)
            _enumValues[en.FullName + "." + value.Name] = (en, value.Number);
    }

    private void RegisterExtension(FieldDescriptor ext, ExtensionRegistry extensionRegistry)
    {
        _extensions[ext.FullName] = ext;
        if (!_extensionsByExtendee.TryGetValue(ext.ExtendeeType.FullName, out var list))
            _extensionsByExtendee[ext.ExtendeeType.FullName] = list = new List<FieldDescriptor>();
        list.Add(ext);
        if (ext.Extension != null)
            extensionRegistry.Add(ext.Extension);
    }

    public override bool HasMessage(string fullName) => _messages.ContainsKey(fullName);

    public MessageDescriptor? FindMessage(string fullName) =>
        _messages.TryGetValue(fullName, out var d) ? d : null;

    public bool HasEnum(string fullName) => _enums.ContainsKey(fullName);

    public EnumDescriptor? FindEnum(string fullName) => _enums.TryGetValue(fullName, out var e) ? e : null;

    /// <summary>
    /// A field of a message by name, or an extension by its full name (the form the escaped
    /// identifier syntax <c>msg.`pkg.ext`</c> produces).
    /// </summary>
    public FieldDescriptor? FindField(MessageDescriptor message, string name)
    {
        var field = message.FindFieldByName(name);
        if (field != null)
            return field;
        if (name.Contains('.') && _extensions.TryGetValue(name, out var ext) && ext.ExtendeeType == message)
            return ext;
        return null;
    }

    /// <summary>The known extensions of a message type.</summary>
    public IReadOnlyList<FieldDescriptor> ExtensionsOf(MessageDescriptor message) =>
        _extensionsByExtendee.TryGetValue(message.FullName, out var list) ? list : Array.Empty<FieldDescriptor>();

    public override CelType? FindFieldType(string messageName, string fieldName)
    {
        if (!_messages.TryGetValue(messageName, out var message))
            return null;
        var field = FindField(message, fieldName);
        return field == null ? null : FieldType(field);
    }

    /// <summary>The CEL type of a field, following the specification's conversion table.</summary>
    public CelType FieldType(FieldDescriptor field)
    {
        if (field.IsMap)
        {
            var entry = field.MessageType;
            return CelType.Map(SingularType(entry.FindFieldByNumber(1)), SingularType(entry.FindFieldByNumber(2)));
        }
        if (field.IsRepeated)
            return CelType.List(SingularType(field));
        return SingularType(field);
    }

    /// <summary>The CEL type of one element of a field: a repeated item, a map key or value, or a singular value.</summary>
    public CelType ElementType(FieldDescriptor field) => SingularType(field);

    private CelType SingularType(FieldDescriptor field)
    {
        switch (field.FieldType)
        {
            case Google.Protobuf.Reflection.FieldType.Message:
            case Google.Protobuf.Reflection.FieldType.Group:
                return CelType.Message(field.MessageType.FullName);
            case Google.Protobuf.Reflection.FieldType.Enum:
                return EnumMode == EnumMode.Strong ? EnumType(field.EnumType) : CelType.Int;
            default:
                return ScalarType(field.FieldType);
        }
    }

    public static CelType ScalarType(Google.Protobuf.Reflection.FieldType type) => type switch
    {
        Google.Protobuf.Reflection.FieldType.Bool => CelType.Bool,
        Google.Protobuf.Reflection.FieldType.Bytes => CelType.Bytes,
        Google.Protobuf.Reflection.FieldType.Double => CelType.Double,
        Google.Protobuf.Reflection.FieldType.Float => CelType.Double,
        Google.Protobuf.Reflection.FieldType.Int32 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.Int64 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.SInt32 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.SInt64 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.SFixed32 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.SFixed64 => CelType.Int,
        Google.Protobuf.Reflection.FieldType.UInt32 => CelType.Uint,
        Google.Protobuf.Reflection.FieldType.UInt64 => CelType.Uint,
        Google.Protobuf.Reflection.FieldType.Fixed32 => CelType.Uint,
        Google.Protobuf.Reflection.FieldType.Fixed64 => CelType.Uint,
        Google.Protobuf.Reflection.FieldType.String => CelType.String,
        Google.Protobuf.Reflection.FieldType.Enum => CelType.Int,
        _ => CelType.Dyn,
    };

    /// <summary>The checker type of an enum in strong mode: an opaque type named after the enum.</summary>
    public static CelType EnumType(EnumDescriptor en) => CelType.Opaque(en.FullName);

    public override bool TryFindEnumValue(string fullName, out long value)
    {
        if (_enumValues.TryGetValue(fullName, out var found))
        {
            value = found.Number;
            return true;
        }
        value = 0;
        return false;
    }

    public override bool TryFindEnumConstant(string fullName, out CelType type, out object constant)
    {
        if (_enumValues.TryGetValue(fullName, out var found))
        {
            if (EnumMode == EnumMode.Strong)
            {
                type = EnumType(found.Enum);
                constant = new EnumValue(found.Enum.FullName, found.Number);
            }
            else
            {
                type = CelType.Int;
                constant = (long)found.Number;
            }
            return true;
        }
        type = CelType.Error;
        constant = null!;
        return false;
    }

    public IMessageFactory? FindMessageFactory(string typeName)
    {
        if (!_messages.TryGetValue(typeName, out var descriptor))
            return null;
        lock (_factories)
        {
            if (!_factories.TryGetValue(typeName, out var factory))
            {
                factory = new MessageFactory(this, descriptor);
                _factories[typeName] = factory;
            }
            return factory;
        }
    }

    /// <summary>Converts a message into its CEL value, unwrapping the well-known types.</summary>
    public CelValue ToCelValue(IMessage message) => ProtoValues.FromMessage(this, message);

    /// <summary>
    /// The functions of the strong enum mode: for each enum, a conversion from int or string
    /// named after the enum, and an <c>int</c> overload accepting it.
    /// </summary>
    public IEnumerable<(FunctionDecl Decl, ICelFunction Function)> EnumFunctions()
    {
        if (EnumMode != EnumMode.Strong)
            yield break;
        foreach (var en in _enums.Values)
        {
            var enumType = EnumType(en);
            var decl = new FunctionDecl(en.FullName)
                .AddOverload(en.FullName + "_from_int", enumType, CelType.Int)
                .AddOverload(en.FullName + "_from_string", enumType, CelType.String);
            var descriptor = en;
            var fn = new DelegateFunction(en.FullName, (ctx, a) =>
            {
                if (a.Length != 1) return ErrorValue.NoSuchOverload(descriptor.FullName, a);
                switch (a[0])
                {
                    case IntValue i:
                        if (i.Value < int.MinValue || i.Value > int.MaxValue)
                            return new ErrorValue("range error converting " + i.Value + " to " + descriptor.FullName);
                        return new EnumValue(descriptor.FullName, (int)i.Value);
                    case StringValue s:
                    {
                        var value = descriptor.FindValueByName(s.Value);
                        return value == null
                            ? new ErrorValue("invalid enum value name '" + s.Value + "' for " + descriptor.FullName)
                            : new EnumValue(descriptor.FullName, value.Number);
                    }
                    case EnumValue e when e.EnumTypeName == descriptor.FullName:
                        return e;
                    default:
                        return ErrorValue.NoSuchOverload(descriptor.FullName, a[0]);
                }
            });
            yield return (decl, fn);
            yield return (new FunctionDecl(FunctionNames.Int).AddOverload(en.FullName + "_to_int", CelType.Int, enumType),
                new DelegateFunction(FunctionNames.Int, (ctx, a) => ErrorValue.NoSuchOverload(FunctionNames.Int, a)));
        }
    }

    private sealed class MessageFactory : IMessageFactory
    {
        private readonly ProtoTypeProvider _provider;
        private readonly MessageDescriptor _descriptor;

        public MessageFactory(ProtoTypeProvider provider, MessageDescriptor descriptor)
        {
            _provider = provider;
            _descriptor = descriptor;
        }

        public CelValue Create(string[] fields, CelValue[] values)
        {
            var message = _descriptor.Parser.ParseFrom(Array.Empty<byte>());
            for (int i = 0; i < fields.Length; i++)
            {
                var field = _provider.FindField(_descriptor, fields[i]);
                if (field == null)
                    return new ErrorValue("no such field '" + fields[i] + "' in " + _descriptor.FullName);
                var error = ProtoValues.SetField(_provider, message, field, values[i]);
                if (error != null)
                    return error;
            }
            return _provider.ToCelValue(message);
        }
    }
}
