using System;
using System.Collections.Generic;
using System.Text;

namespace ConnectNet.Validation.Cel.Checker;

internal enum TypeKind
{
    Dyn,
    Any,
    Bool,
    Bytes,
    Double,
    Duration,
    Error,
    Int,
    List,
    Map,
    Null,
    String,
    Struct,
    Timestamp,
    Type,
    TypeParam,
    Uint,
    Opaque,
}

/// <summary>
/// A CEL type as seen by the checker. Instances are immutable; the well-known scalar types are
/// singletons. A primitive marked <see cref="IsNullable"/> is a protobuf wrapper type
/// (<c>wrapper(int)</c>): it accepts null in addition to its primitive, and is otherwise exactly
/// its primitive for overload resolution.
/// </summary>
internal sealed class CelType : IEquatable<CelType>
{
    private static readonly CelType[] NoParams = Array.Empty<CelType>();

    private CelType(TypeKind kind, string name, IReadOnlyList<CelType>? parameters = null, bool nullable = false)
    {
        Kind = kind;
        Name = name;
        Parameters = parameters ?? NoParams;
        IsNullable = nullable;
    }

    public TypeKind Kind { get; }

    /// <summary>The runtime type name: <c>int</c>, <c>list</c>, a message's full name, …</summary>
    public string Name { get; }

    public IReadOnlyList<CelType> Parameters { get; }

    /// <summary>True for protobuf wrapper types, which accept <c>null</c>.</summary>
    public bool IsNullable { get; }

    public static readonly CelType Dyn = new(TypeKind.Dyn, "dyn");
    public static readonly CelType Any = new(TypeKind.Any, "google.protobuf.Any");
    public static readonly CelType Bool = new(TypeKind.Bool, "bool");
    public static readonly CelType Bytes = new(TypeKind.Bytes, "bytes");
    public static readonly CelType Double = new(TypeKind.Double, "double");
    public static readonly CelType Duration = new(TypeKind.Duration, "google.protobuf.Duration");
    public static readonly CelType Error = new(TypeKind.Error, "!error!");
    public static readonly CelType Int = new(TypeKind.Int, "int");
    public static readonly CelType Null = new(TypeKind.Null, "null_type");
    public static readonly CelType String = new(TypeKind.String, "string");
    public static readonly CelType Timestamp = new(TypeKind.Timestamp, "google.protobuf.Timestamp");
    public static readonly CelType Uint = new(TypeKind.Uint, "uint");

    /// <summary>The type of types, with no parameter: <c>type</c>.</summary>
    public static readonly CelType TypeType = new(TypeKind.Type, "type");

    public static readonly CelType BoolWrapper = new(TypeKind.Bool, "bool", null, nullable: true);
    public static readonly CelType BytesWrapper = new(TypeKind.Bytes, "bytes", null, nullable: true);
    public static readonly CelType DoubleWrapper = new(TypeKind.Double, "double", null, nullable: true);
    public static readonly CelType IntWrapper = new(TypeKind.Int, "int", null, nullable: true);
    public static readonly CelType StringWrapper = new(TypeKind.String, "string", null, nullable: true);
    public static readonly CelType UintWrapper = new(TypeKind.Uint, "uint", null, nullable: true);

    public static readonly CelType ListOfDyn = List(Dyn);
    public static readonly CelType MapOfDynDyn = Map(Dyn, Dyn);
    public static readonly CelType MapOfStringDyn = Map(String, Dyn);

    public static CelType List(CelType element) => new(TypeKind.List, "list", new[] { element });

    public static CelType Map(CelType key, CelType value) => new(TypeKind.Map, "map", new[] { key, value });

    public static CelType TypeParam(string name) => new(TypeKind.TypeParam, name);

    /// <summary>The type of a type value: <c>type(T)</c>.</summary>
    public static CelType TypeOf(CelType type) => new(TypeKind.Type, "type", new[] { type });

    public static CelType Opaque(string name, params CelType[] parameters) => new(TypeKind.Opaque, name, parameters);

    private static readonly Dictionary<string, CelType> WellKnownMessages = new(StringComparer.Ordinal)
    {
        ["google.protobuf.BoolValue"] = BoolWrapper,
        ["google.protobuf.BytesValue"] = BytesWrapper,
        ["google.protobuf.DoubleValue"] = DoubleWrapper,
        ["google.protobuf.FloatValue"] = DoubleWrapper,
        ["google.protobuf.Int64Value"] = IntWrapper,
        ["google.protobuf.Int32Value"] = IntWrapper,
        ["google.protobuf.UInt64Value"] = UintWrapper,
        ["google.protobuf.UInt32Value"] = UintWrapper,
        ["google.protobuf.StringValue"] = StringWrapper,
        ["google.protobuf.Any"] = Any,
        ["google.protobuf.Duration"] = Duration,
        ["google.protobuf.Timestamp"] = Timestamp,
        ["google.protobuf.ListValue"] = ListOfDyn,
        ["google.protobuf.NullValue"] = Null,
        ["google.protobuf.Struct"] = MapOfStringDyn,
        ["google.protobuf.Value"] = Dyn,
    };

    /// <summary>
    /// The type of a protobuf message by full name. Well-known types map to their CEL
    /// counterparts (wrappers, timestamp, duration, JSON types); any other name is a struct type.
    /// </summary>
    public static CelType Message(string fullName)
    {
        if (fullName.Length > 0 && fullName[0] == '.')
            fullName = fullName.Substring(1);
        if (WellKnownMessages.TryGetValue(fullName, out var wkt))
            return wkt;
        return new CelType(TypeKind.Struct, fullName);
    }

    public static bool TryGetWellKnownMessage(string fullName, out CelType type) =>
        WellKnownMessages.TryGetValue(fullName, out type!);

    /// <summary>The protobuf message name that a well-known CEL type is constructed from, or null.</summary>
    public string? WellKnownMessageName => Kind switch
    {
        TypeKind.Any => "google.protobuf.Any",
        TypeKind.Timestamp => "google.protobuf.Timestamp",
        TypeKind.Duration => "google.protobuf.Duration",
        TypeKind.Dyn => "google.protobuf.Value",
        TypeKind.Null => "google.protobuf.NullValue",
        TypeKind.Bool when IsNullable => "google.protobuf.BoolValue",
        TypeKind.Bytes when IsNullable => "google.protobuf.BytesValue",
        TypeKind.Double when IsNullable => "google.protobuf.DoubleValue",
        TypeKind.Int when IsNullable => "google.protobuf.Int64Value",
        TypeKind.String when IsNullable => "google.protobuf.StringValue",
        TypeKind.Uint when IsNullable => "google.protobuf.UInt64Value",
        TypeKind.List when Parameters[0].Kind == TypeKind.Dyn => "google.protobuf.ListValue",
        TypeKind.Map when Parameters[0].Kind == TypeKind.String && Parameters[1].Kind == TypeKind.Dyn => "google.protobuf.Struct",
        _ => null,
    };

    public bool IsDyn => Kind is TypeKind.Dyn or TypeKind.Any;

    public bool IsDynOrError => IsDyn || Kind == TypeKind.Error;

    public bool IsPrimitive => Kind is TypeKind.Bool or TypeKind.Bytes or TypeKind.Double or TypeKind.Int
        or TypeKind.String or TypeKind.Uint;

    /// <summary>Exact structural equality, including type parameter names and ignoring nullability.</summary>
    public bool IsExactType(CelType other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Kind != other.Kind || Parameters.Count != other.Parameters.Count) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (!Parameters[i].IsExactType(other.Parameters[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a value of <paramref name="from"/> may be used where this type is expected,
    /// without type parameter inference: identity, dyn, or structurally equal with assignable
    /// parameters. Wrapper types additionally accept null.
    /// </summary>
    public bool IsAssignableFrom(CelType from)
    {
        if (ReferenceEquals(this, from) || IsDyn) return true;
        if (IsNullable && from.Kind == TypeKind.Null) return true;
        if (Kind != from.Kind || !string.Equals(Name, from.Name, StringComparison.Ordinal)
            || Parameters.Count != from.Parameters.Count)
            return false;
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (!Parameters[i].IsAssignableFrom(from.Parameters[i])) return false;
        }
        return true;
    }

    public bool Equals(CelType? other) => other != null && IsExactType(other) && IsNullable == other.IsNullable;

    public override bool Equals(object? obj) => obj is CelType t && Equals(t);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Name);
        hash.Add(IsNullable);
        foreach (var p in Parameters) hash.Add(p);
        return hash.ToHashCode();
    }

    /// <summary>Renders the type as the checker's diagnostics do: <c>list(int)</c>, <c>wrapper(int)</c>, <c>type(string)</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Format(sb);
        return sb.ToString();
    }

    private void Format(StringBuilder sb)
    {
        switch (Kind)
        {
            case TypeKind.Null:
                sb.Append("null");
                return;
            case TypeKind.Any:
                sb.Append("any");
                return;
            case TypeKind.Duration:
                sb.Append("duration");
                return;
            case TypeKind.Timestamp:
                sb.Append("timestamp");
                return;
            case TypeKind.Type:
                sb.Append("type");
                break;
            case TypeKind.TypeParam:
                sb.Append(Name);
                return;
            default:
                if (IsNullable)
                {
                    sb.Append("wrapper(").Append(Name).Append(')');
                    return;
                }
                sb.Append(Name);
                break;
        }
        if (Parameters.Count > 0)
        {
            sb.Append('(');
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Parameters[i].Format(sb);
            }
            sb.Append(')');
        }
    }
}
