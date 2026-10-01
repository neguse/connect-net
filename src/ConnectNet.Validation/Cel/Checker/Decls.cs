using System;
using System.Collections.Generic;

namespace ConnectNet.Validation.Cel.Checker;

/// <summary>A declared variable or constant.</summary>
internal sealed class VariableDecl
{
    public VariableDecl(string name, CelType type, object? constantValue = null)
    {
        Name = name;
        Type = type;
        ConstantValue = constantValue;
        IsConstant = constantValue != null;
    }

    public string Name { get; }

    public CelType Type { get; }

    /// <summary>Compile-time value for constants such as enum values (a <see cref="long"/>).</summary>
    public object? ConstantValue { get; }

    public bool IsConstant { get; }
}

/// <summary>One overload of a function: an id, the argument types and the result type.</summary>
internal sealed class OverloadDecl
{
    public OverloadDecl(string id, IReadOnlyList<CelType> argTypes, CelType resultType, bool isMemberFunction)
    {
        Id = id;
        ArgTypes = argTypes;
        ResultType = resultType;
        IsMemberFunction = isMemberFunction;
        TypeParams = CollectTypeParams(argTypes, resultType);
    }

    public string Id { get; }

    /// <summary>For member functions the receiver is the first argument.</summary>
    public IReadOnlyList<CelType> ArgTypes { get; }

    public CelType ResultType { get; }

    public bool IsMemberFunction { get; }

    public IReadOnlyList<string> TypeParams { get; }

    private static IReadOnlyList<string> CollectTypeParams(IReadOnlyList<CelType> argTypes, CelType resultType)
    {
        var names = new List<string>();
        void Visit(CelType t)
        {
            if (t.Kind == TypeKind.TypeParam)
            {
                if (!names.Contains(t.Name)) names.Add(t.Name);
                return;
            }
            foreach (var p in t.Parameters) Visit(p);
        }
        foreach (var a in argTypes) Visit(a);
        Visit(resultType);
        return names;
    }
}

internal sealed class FunctionDecl
{
    private readonly List<OverloadDecl> _overloads = new();

    public FunctionDecl(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<OverloadDecl> Overloads => _overloads;

    public FunctionDecl AddOverload(string id, CelType resultType, params CelType[] argTypes)
    {
        _overloads.Add(new OverloadDecl(id, argTypes, resultType, isMemberFunction: false));
        return this;
    }

    public FunctionDecl AddMemberOverload(string id, CelType resultType, params CelType[] argTypes)
    {
        _overloads.Add(new OverloadDecl(id, argTypes, resultType, isMemberFunction: true));
        return this;
    }

    public FunctionDecl Add(OverloadDecl overload)
    {
        _overloads.Add(overload);
        return this;
    }

    /// <summary>Merges the overloads of another declaration of the same function into this one.</summary>
    public FunctionDecl Merge(FunctionDecl other)
    {
        foreach (var o in other._overloads)
        {
            bool dup = false;
            foreach (var existing in _overloads)
            {
                if (existing.Id == o.Id)
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) _overloads.Add(o);
        }
        return this;
    }
}

/// <summary>
/// Supplies message types, their fields, and enum values to the checker. The protobuf-backed
/// implementation lives in the Protobuf adaptation layer; this base resolves only the
/// well-known type names.
/// </summary>
internal abstract class TypeProvider
{
    private static readonly Dictionary<string, CelType> TypeNames = new(StringComparer.Ordinal)
    {
        ["bool"] = CelType.Bool,
        ["bytes"] = CelType.Bytes,
        ["double"] = CelType.Double,
        ["google.protobuf.Duration"] = CelType.Duration,
        ["int"] = CelType.Int,
        ["list"] = CelType.ListOfDyn,
        ["map"] = CelType.MapOfDynDyn,
        ["null_type"] = CelType.Null,
        ["string"] = CelType.String,
        ["google.protobuf.Timestamp"] = CelType.Timestamp,
        ["type"] = CelType.TypeType,
        ["uint"] = CelType.Uint,
        ["google.protobuf.Any"] = CelType.Any,
        ["dyn"] = CelType.Dyn,
    };

    /// <summary>
    /// Resolves a type by its name as used in identifiers: the primitive names, the well-known
    /// message names, and any message known to the provider. Returns null when unknown.
    /// </summary>
    public CelType? FindType(string name)
    {
        if (name.Length > 0 && name[0] == '.')
            name = name.Substring(1);
        if (TypeNames.TryGetValue(name, out var t))
            return t;
        if (CelType.TryGetWellKnownMessage(name, out var wkt) && HasMessage(name))
            return wkt;
        return HasMessage(name) ? CelType.Message(name) : null;
    }

    /// <summary>Whether a message with this full name is known.</summary>
    public abstract bool HasMessage(string fullName);

    /// <summary>The CEL type of a message field, or null when the field does not exist.</summary>
    public abstract CelType? FindFieldType(string messageName, string fieldName);

    /// <summary>The integer value of a fully qualified enum value name (<c>pkg.Enum.VALUE</c>).</summary>
    public abstract bool TryFindEnumValue(string fullName, out long value);
}

/// <summary>A provider that knows no messages; the well-known types still resolve.</summary>
internal sealed class EmptyTypeProvider : TypeProvider
{
    public static readonly EmptyTypeProvider Instance = new();

    private static readonly HashSet<string> WellKnown = new(StringComparer.Ordinal)
    {
        "google.protobuf.Any", "google.protobuf.Duration", "google.protobuf.Timestamp",
        "google.protobuf.BoolValue", "google.protobuf.BytesValue", "google.protobuf.DoubleValue",
        "google.protobuf.FloatValue", "google.protobuf.Int64Value", "google.protobuf.Int32Value",
        "google.protobuf.UInt64Value", "google.protobuf.UInt32Value", "google.protobuf.StringValue",
        "google.protobuf.ListValue", "google.protobuf.NullValue", "google.protobuf.Struct", "google.protobuf.Value",
    };

    public override bool HasMessage(string fullName) => WellKnown.Contains(fullName);

    public override CelType? FindFieldType(string messageName, string fieldName) => null;

    public override bool TryFindEnumValue(string fullName, out long value)
    {
        value = 0;
        return false;
    }
}

/// <summary>
/// The namespace in which simple names are resolved. A name <c>x</c> in container <c>a.b</c>
/// is tried as <c>a.b.x</c>, <c>a.x</c>, then <c>x</c>; a leading dot forces the root.
/// </summary>
internal sealed class Container
{
    public static readonly Container Root = new("");

    public Container(string name)
    {
        Name = name ?? "";
    }

    public string Name { get; }

    public IReadOnlyList<string> ResolveCandidateNames(string name)
    {
        if (name.Length > 0 && name[0] == '.')
            return new[] { name.Substring(1) };
        if (Name.Length == 0)
            return new[] { name };
        var candidates = new List<string>();
        var next = Name;
        candidates.Add(next + "." + name);
        for (int i = next.LastIndexOf('.'); i >= 0; i = next.LastIndexOf('.'))
        {
            next = next.Substring(0, i);
            candidates.Add(next + "." + name);
        }
        candidates.Add(name);
        return candidates;
    }
}
