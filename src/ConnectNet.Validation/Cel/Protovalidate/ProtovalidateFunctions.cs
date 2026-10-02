using System;
using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Runtime;

namespace ConnectNet.Validation.Cel.Protovalidate;

/// <summary>
/// The functions every Protovalidate implementation must provide beyond the CEL standard
/// library, with the overloads the reference library declares, plus the <c>now</c> variable.
/// </summary>
internal static class ProtovalidateFunctions
{
    public const string NowVariable = "now";

    public static CelEnvironment AddTo(CelEnvironment env)
    {
        env.AddVariable(NowVariable, CelType.Timestamp);
        foreach (var (decl, fn) in Declarations())
            env.AddFunction(decl, fn);
        return env;
    }

    public static IEnumerable<(FunctionDecl Decl, ICelFunction Function)> Declarations()
    {
        var s = CelType.String;
        var b = CelType.Bool;
        var i = CelType.Int;
        var d = CelType.Double;

        var unique = new FunctionDecl("unique");
        foreach (var (t, name) in new[] { (CelType.Bool, "bool"), (CelType.Int, "int"), (CelType.Uint, "uint"), (CelType.Double, "double"), (CelType.String, "string"), (CelType.Bytes, "bytes") })
            unique.AddMemberOverload(name + "_unique_bool", b, CelType.List(t));
        yield return (unique, new DelegateFunction("unique", Unique));

        yield return (new FunctionDecl("getField").AddOverload("get_field_any_string", CelType.Dyn, CelType.Dyn, s),
            new DelegateFunction("getField", GetField));

        yield return (new FunctionDecl("isNan").AddMemberOverload("double_is_nan_bool", b, d),
            new DelegateFunction("isNan", (ctx, a) =>
                a.Length == 1 && a[0] is DoubleValue v ? BoolValue.Of(double.IsNaN(v.Value)) : ErrorValue.NoSuchOverload("isNan", a)));

        yield return (new FunctionDecl("isInf")
                .AddMemberOverload("double_is_inf_bool", b, d)
                .AddMemberOverload("double_int_is_inf_bool", b, d, i),
            new DelegateFunction("isInf", (ctx, a) =>
            {
                if (a.Length >= 1 && a[0] is DoubleValue v)
                {
                    if (a.Length == 1)
                        return BoolValue.Of(double.IsInfinity(v.Value));
                    if (a.Length == 2 && a[1] is IntValue sign)
                    {
                        return BoolValue.Of(sign.Value > 0 ? double.IsPositiveInfinity(v.Value)
                            : sign.Value < 0 ? double.IsNegativeInfinity(v.Value)
                            : double.IsInfinity(v.Value));
                    }
                }
                return ErrorValue.NoSuchOverload("isInf", a);
            }));

        yield return (new FunctionDecl("isHostname").AddMemberOverload("string_is_hostname_bool", b, s),
            new DelegateFunction("isHostname", (ctx, a) => StringCheck("isHostname", a, StringRules.IsHostname)));
        yield return (new FunctionDecl("isEmail").AddMemberOverload("string_is_email_bool", b, s),
            new DelegateFunction("isEmail", (ctx, a) => StringCheck("isEmail", a, StringRules.IsEmail)));
        yield return (new FunctionDecl("isUri").AddMemberOverload("string_is_uri_bool", b, s),
            new DelegateFunction("isUri", (ctx, a) => StringCheck("isUri", a, StringRules.IsUri)));
        yield return (new FunctionDecl("isUriRef").AddMemberOverload("string_is_uri_ref_bool", b, s),
            new DelegateFunction("isUriRef", (ctx, a) => StringCheck("isUriRef", a, StringRules.IsUriRef)));

        yield return (new FunctionDecl("isIp")
                .AddMemberOverload("string_is_ip_bool", b, s)
                .AddMemberOverload("string_int_is_ip_bool", b, s, i),
            new DelegateFunction("isIp", (ctx, a) =>
            {
                if (a.Length >= 1 && a[0] is StringValue v)
                {
                    ctx.Consume(v.Value.Length);
                    if (a.Length == 1) return BoolValue.Of(StringRules.IsIp(v.Value, 0));
                    if (a.Length == 2 && a[1] is IntValue version) return BoolValue.Of(StringRules.IsIp(v.Value, version.Value));
                }
                return ErrorValue.NoSuchOverload("isIp", a);
            }));

        yield return (new FunctionDecl("isIpPrefix")
                .AddMemberOverload("string_is_ip_prefix_bool", b, s)
                .AddMemberOverload("string_int_is_ip_prefix_bool", b, s, i)
                .AddMemberOverload("string_bool_is_ip_prefix_bool", b, s, b)
                .AddMemberOverload("string_int_bool_is_ip_prefix_bool", b, s, i, b),
            new DelegateFunction("isIpPrefix", (ctx, a) =>
            {
                if (a.Length >= 1 && a[0] is StringValue v)
                {
                    ctx.Consume(v.Value.Length);
                    switch (a.Length)
                    {
                        case 1:
                            return BoolValue.Of(StringRules.IsIpPrefix(v.Value, 0, false));
                        case 2 when a[1] is IntValue version:
                            return BoolValue.Of(StringRules.IsIpPrefix(v.Value, version.Value, false));
                        case 2 when a[1] is BoolValue strict:
                            return BoolValue.Of(StringRules.IsIpPrefix(v.Value, 0, strict.Value));
                        case 3 when a[1] is IntValue version && a[2] is BoolValue strict:
                            return BoolValue.Of(StringRules.IsIpPrefix(v.Value, version.Value, strict.Value));
                    }
                }
                return ErrorValue.NoSuchOverload("isIpPrefix", a);
            }));

        yield return (new FunctionDecl("isHostAndPort").AddMemberOverload("string_bool_is_host_and_port_bool", b, s, b),
            new DelegateFunction("isHostAndPort", (ctx, a) =>
            {
                if (a.Length == 2 && a[0] is StringValue v && a[1] is BoolValue portRequired)
                {
                    ctx.Consume(v.Value.Length);
                    return BoolValue.Of(StringRules.IsHostAndPort(v.Value, portRequired.Value));
                }
                return ErrorValue.NoSuchOverload("isHostAndPort", a);
            }));

        // Bytes overloads of the standard string tests. The runtime functions already accept
        // bytes; only the declarations are added here.
        yield return (new FunctionDecl(FunctionNames.Contains).AddMemberOverload("contains_bytes", b, CelType.Bytes, CelType.Bytes),
            new BytesTestFunction(FunctionNames.Contains, (x, y) => x.AsSpan().IndexOf(y) >= 0));
        yield return (new FunctionDecl(FunctionNames.StartsWith).AddMemberOverload("starts_with_bytes", b, CelType.Bytes, CelType.Bytes),
            new BytesTestFunction(FunctionNames.StartsWith, (x, y) => x.AsSpan().StartsWith(y)));
        yield return (new FunctionDecl(FunctionNames.EndsWith).AddMemberOverload("ends_with_bytes", b, CelType.Bytes, CelType.Bytes),
            new BytesTestFunction(FunctionNames.EndsWith, (x, y) => x.AsSpan().EndsWith(y)));
    }

    private static CelValue StringCheck(string name, CelValue[] a, Func<string, bool> check)
    {
        if (a.Length == 1 && a[0] is StringValue v)
            return BoolValue.Of(check(v.Value));
        return ErrorValue.NoSuchOverload(name, a);
    }

    private static CelValue Unique(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 1 || a[0] is not ListValue list)
            return ErrorValue.NoSuchOverload("unique", a);
        ctx.Consume(list.Count);
        if (list.Count <= 1)
            return BoolValue.True;
        var seen = new HashSet<UniqueKey>();
        foreach (var element in list.Elements)
        {
            // The reference keys a Go map by the value, so NaN never matches anything (not
            // even itself) and -0.0 and 0.0 are the same key.
            if (element is DoubleValue { Value: double.NaN })
                continue;
            if (!UniqueKey.TryCreate(element, out var key))
                return new ErrorValue("unique: unsupported element type " + element.TypeName);
            if (!seen.Add(key))
                return BoolValue.False;
        }
        return BoolValue.True;
    }

    /// <summary>Strict-type identity of a scalar for uniqueness: 1 and 1u and 1.0 are distinct, as in the reference.</summary>
    private readonly struct UniqueKey : IEquatable<UniqueKey>
    {
        private readonly string _type;
        private readonly long _bits;
        private readonly string? _text;

        private UniqueKey(string type, long bits, string? text)
        {
            _type = type;
            _bits = bits;
            _text = text;
        }

        public static bool TryCreate(CelValue v, out UniqueKey key)
        {
            switch (v)
            {
                case BoolValue b: key = new UniqueKey("bool", b.Value ? 1 : 0, null); return true;
                case IntValue i: key = new UniqueKey("int", i.Value, null); return true;
                case UintValue u: key = new UniqueKey("uint", unchecked((long)u.Value), null); return true;
                case DoubleValue d: key = new UniqueKey("double", BitConverter.DoubleToInt64Bits(d.Value == 0 ? 0.0 : d.Value), null); return true;
                case StringValue s: key = new UniqueKey("string", 0, s.Value); return true;
                case BytesValue by: key = new UniqueKey("bytes", 0, Convert.ToBase64String(by.Value)); return true;
                default: key = default; return false;
            }
        }

        public bool Equals(UniqueKey other) => _type == other._type && _bits == other._bits && string.Equals(_text, other._text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is UniqueKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(_type, _bits, _text);
    }

    private static CelValue GetField(EvalContext ctx, CelValue[] a)
    {
        if (a.Length != 2 || a[1] is not StringValue name)
            return ErrorValue.NoSuchOverload("getField", a);
        switch (a[0])
        {
            case MessageValue message:
                return message.GetField(name.Value);
            case MapValue map:
                return map.TryGet(name, out var value) ? value : new ErrorValue("no such key: " + name.Value);
            default:
                return ErrorValue.NoSuchOverload("getField", a);
        }
    }

    /// <summary>Wraps the standard string test so the same name also serves the bytes overload.</summary>
    private sealed class BytesTestFunction : ICelFunction
    {
        private readonly Func<byte[], byte[], bool> _test;

        public BytesTestFunction(string name, Func<byte[], byte[], bool> test)
        {
            Name = name;
            _test = test;
        }

        public string Name { get; }

        public CelValue Invoke(EvalContext ctx, CelValue[] args)
        {
            if (args.Length == 2 && args[0] is BytesValue x && args[1] is BytesValue y)
            {
                ctx.Consume(x.Value.Length);
                return BoolValue.Of(_test(x.Value, y.Value));
            }
            if (args.Length == 2 && args[0] is StringValue sx && args[1] is StringValue sy)
            {
                return Name switch
                {
                    FunctionNames.Contains => BoolValue.Of(sx.Value.Contains(sy.Value, StringComparison.Ordinal)),
                    FunctionNames.StartsWith => BoolValue.Of(sx.Value.StartsWith(sy.Value, StringComparison.Ordinal)),
                    _ => BoolValue.Of(sx.Value.EndsWith(sy.Value, StringComparison.Ordinal)),
                };
            }
            return ErrorValue.NoSuchOverload(Name, args);
        }
    }
}
