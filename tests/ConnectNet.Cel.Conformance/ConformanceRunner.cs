using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cel.Expr;
using Cel.Expr.Conformance.Test;
using ConnectNet.Validation.Cel;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Runtime;
using ConnectNet.Validation.Cel.Syntax;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Rt = ConnectNet.Validation.Cel.Runtime;
using Wkt = Google.Protobuf.WellKnownTypes;
using Value = Cel.Expr.Value;

namespace ConnectNet.Cel.Conformance;

/// <summary>Outcome of one corpus case.</summary>
internal sealed class CaseResult
{
    public CaseResult(string name, bool passed, string detail)
    {
        Name = name;
        Passed = passed;
        Detail = detail;
    }

    public string Name { get; }
    public bool Passed { get; }
    public string Detail { get; }
}

internal sealed class FileResult
{
    public FileResult(string file, IReadOnlyList<CaseResult> cases)
    {
        File = file;
        Cases = cases;
    }

    public string File { get; }
    public IReadOnlyList<CaseResult> Cases { get; }
    public int Total => Cases.Count;
    public int Passed => Cases.Count(c => c.Passed);
    public IEnumerable<CaseResult> Failures => Cases.Where(c => !c.Passed);
}

/// <summary>
/// Runs the cel-spec simple conformance corpus against the parser, checker and evaluator.
/// Mirrors the reference runner: a check error or an evaluation error both satisfy an error
/// expectation; a value expectation requires a successful check (unless disabled) and a
/// strictly equal result.
/// </summary>
internal static class ConformanceRunner
{
    /// <summary>Message types the corpus may reference, by full name.</summary>
    /// <summary>The schema files of the corpus, in dependency order.</summary>
    public static readonly FileDescriptor[] Files =
    {
        SyntaxReflection.Descriptor, CheckedReflection.Descriptor, ValueReflection.Descriptor,
        EvalReflection.Descriptor, SimpleReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto2.TestAllTypesReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto2.TestAllTypesExtensionsReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto3.TestAllTypesReflection.Descriptor,
    };

    public static readonly TypeRegistry Registry = TypeRegistry.FromFiles(
        SyntaxReflection.Descriptor, CheckedReflection.Descriptor, ValueReflection.Descriptor,
        EvalReflection.Descriptor, SimpleReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto2.TestAllTypesReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto2.TestAllTypesExtensionsReflection.Descriptor,
        global::Cel.Expr.Conformance.Proto3.TestAllTypesReflection.Descriptor,
        Wkt.AnyReflection.Descriptor, Wkt.DurationReflection.Descriptor, Wkt.TimestampReflection.Descriptor,
        Wkt.StructReflection.Descriptor, Wkt.WrappersReflection.Descriptor, Wkt.EmptyReflection.Descriptor,
        Wkt.FieldMaskReflection.Descriptor);

    /// <summary>The number of cases in each required file at the pinned revision; a short read cannot pass.</summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedCounts = new Dictionary<string, int>
    {
        ["basic"] = 43,
        ["comparisons"] = 406,
        ["conversions"] = 109,
        ["dynamic"] = 226,
        ["enums"] = 85,
        ["fields"] = 60,
        ["fp_math"] = 30,
        ["integer_math"] = 64,
        ["lists"] = 39,
        ["logic"] = 30,
        ["macros"] = 44,
        ["namespace"] = 14,
        ["parse"] = 219,
        ["plumbing"] = 5,
        ["proto2"] = 118,
        ["proto3"] = 85,
        ["string"] = 51,
        ["string_ext"] = 216,
        ["timestamps"] = 78,
        ["wrappers"] = 36,
    };

    public static string TestDataDirectory =>
        Path.Combine(AppContext.BaseDirectory, "testdata");

    public static SimpleTestFile Load(string file)
    {
        var text = File.ReadAllText(Path.Combine(TestDataDirectory, file + ".textproto"));
        return TextProtoParser.Parse<SimpleTestFile>(text, Registry, Files);
    }

    /// <summary>Hook for the Protobuf adaptation: supplies the provider and message factories for the test messages.</summary>
    public static Func<(TypeProvider Provider, IMessageFactoryProvider? Factories)>? ProtoSupport { get; set; }

    public static FileResult Run(string file, Func<string, bool>? filter = null)
    {
        var testFile = Load(file);
        var results = new List<CaseResult>();
        foreach (var section in testFile.Section)
        {
            foreach (var test in section.Test)
            {
                var name = file + "/" + section.Name + "/" + test.Name;
                if (filter != null && !filter(name))
                    continue;
                string detail;
                bool passed;
                try
                {
                    passed = RunCase(test, out detail);
                }
                catch (Exception e)
                {
                    passed = false;
                    detail = "exception: " + e;
                }
                results.Add(new CaseResult(name, passed, detail));
            }
        }
        return new FileResult(file, results);
    }

    private static bool RunCase(SimpleTest test, out string detail)
    {
        var (provider, factories) = ProtoSupport?.Invoke() ?? (EmptyTypeProvider.Instance, null);
        var parserOptions = new ParserOptions { Macros = test.DisableMacros ? Array.Empty<Macro>() : StandardMacros.All_ };
        var env = new CelEnvironment(new Container(test.Container), provider, factories, parserOptions: parserOptions)
            .AddStandardLibrary()
            .AddStringsExtension();
        foreach (var decl in test.TypeEnv)
            AddDecl(env, decl);

        var parsed = env.Parse(test.Expr);
        if (!parsed.IsSuccess)
            return Expect(test, null, "parse error: " + parsed.Errors.FormatAll(), out detail);

        CelProgram program;
        CelType? deducedType = null;
        if (test.DisableCheck)
        {
            program = env.PlanUnchecked(parsed);
        }
        else
        {
            var checked_ = env.Check(parsed);
            if (!checked_.IsSuccess)
                return Expect(test, null, "check error: " + checked_.Errors.FormatAll(), out detail);
            deducedType = checked_.ResultType;
            if (test.CheckOnly)
                return ExpectType(test, deducedType, out detail);
            program = env.PlanChecked(checked_);
        }

        var bindings = new Dictionary<string, CelValue>();
        foreach (var binding in test.Bindings)
        {
            var value = binding.Value.KindCase == ExprValue.KindOneofCase.Value
                ? ToCelValue(binding.Value.Value)
                : new ErrorValue("binding is not a value");
            bindings[binding.Key] = value;
        }

        CelValue result;
        try
        {
            result = program.Evaluate(Activation.Of(bindings));
        }
        catch (CelEvaluationException e)
        {
            result = new ErrorValue("evaluation failed: " + e.Message);
        }
        if (test.ResultMatcherCase == SimpleTest.ResultMatcherOneofCase.TypedResult && deducedType != null)
        {
            if (!ExpectType(test, deducedType, out detail))
                return false;
        }
        return Expect(test, result, null, out detail);
    }

    private static bool ExpectType(SimpleTest test, CelType deduced, out string detail)
    {
        if (test.ResultMatcherCase != SimpleTest.ResultMatcherOneofCase.TypedResult)
        {
            detail = "check-only test without a typed result";
            return test.CheckOnly;
        }
        var expected = ToCelType(test.TypedResult.DeducedType).ToString();
        var actual = deduced.ToString();
        detail = $"deduced type {actual}, expected {expected}";
        return expected == actual;
    }

    private static bool Expect(SimpleTest test, CelValue? result, string? failure, out string detail)
    {
        bool isError = result == null || result.IsError;
        string actual = failure ?? result!.ToString();
        switch (test.ResultMatcherCase)
        {
            case SimpleTest.ResultMatcherOneofCase.EvalError:
            case SimpleTest.ResultMatcherOneofCase.AnyEvalErrors:
                detail = isError ? "error as expected: " + actual : "expected an error, got " + actual;
                return isError;
            case SimpleTest.ResultMatcherOneofCase.Unknown:
            case SimpleTest.ResultMatcherOneofCase.AnyUnknowns:
                detail = "unknown results are not supported";
                return false;
            case SimpleTest.ResultMatcherOneofCase.Value:
            case SimpleTest.ResultMatcherOneofCase.TypedResult:
            case SimpleTest.ResultMatcherOneofCase.None:
            {
                var expectedProto = test.ResultMatcherCase switch
                {
                    SimpleTest.ResultMatcherOneofCase.Value => test.Value,
                    SimpleTest.ResultMatcherOneofCase.TypedResult => test.TypedResult.Result,
                    _ => new Value { BoolValue = true },
                };
                if (isError)
                {
                    detail = "expected " + expectedProto + ", got " + actual;
                    return false;
                }
                var expected = ToCelValue(expectedProto);
                bool ok = StrictEquals(expected, result!);
                detail = ok ? "ok" : "expected " + expected + ", got " + actual;
                return ok;
            }
            default:
                detail = "unsupported matcher " + test.ResultMatcherCase;
                return false;
        }
    }

    // ---- declarations ----

    private static void AddDecl(CelEnvironment env, Decl decl)
    {
        switch (decl.DeclKindCase)
        {
            case Decl.DeclKindOneofCase.Ident:
                env.AddVariable(new VariableDecl(decl.Name, ToCelType(decl.Ident.Type)));
                break;
            case Decl.DeclKindOneofCase.Function:
            {
                var fn = new FunctionDecl(decl.Name);
                foreach (var o in decl.Function.Overloads)
                {
                    var args = o.Params.Select(ToCelType).ToArray();
                    fn.Add(new OverloadDecl(o.OverloadId, args, ToCelType(o.ResultType), o.IsInstanceFunction));
                }
                env.AddFunction(fn, new DelegateFunction(decl.Name, (ctx, a) => new ErrorValue("no runtime binding for " + decl.Name)));
                break;
            }
        }
    }

    public static CelType ToCelType(global::Cel.Expr.Type type)
    {
        switch (type.TypeKindCase)
        {
            case global::Cel.Expr.Type.TypeKindOneofCase.Dyn:
                return CelType.Dyn;
            case global::Cel.Expr.Type.TypeKindOneofCase.Null:
                return CelType.Null;
            case global::Cel.Expr.Type.TypeKindOneofCase.Primitive:
                return Primitive(type.Primitive, nullable: false);
            case global::Cel.Expr.Type.TypeKindOneofCase.Wrapper:
                return Primitive(type.Wrapper, nullable: true);
            case global::Cel.Expr.Type.TypeKindOneofCase.WellKnown:
                return type.WellKnown switch
                {
                    global::Cel.Expr.Type.Types.WellKnownType.Any => CelType.Any,
                    global::Cel.Expr.Type.Types.WellKnownType.Timestamp => CelType.Timestamp,
                    global::Cel.Expr.Type.Types.WellKnownType.Duration => CelType.Duration,
                    _ => CelType.Error,
                };
            case global::Cel.Expr.Type.TypeKindOneofCase.ListType:
                return CelType.List(ToCelType(type.ListType.ElemType));
            case global::Cel.Expr.Type.TypeKindOneofCase.MapType:
                return CelType.Map(ToCelType(type.MapType.KeyType), ToCelType(type.MapType.ValueType));
            case global::Cel.Expr.Type.TypeKindOneofCase.MessageType:
                return CelType.Message(type.MessageType);
            case global::Cel.Expr.Type.TypeKindOneofCase.TypeParam:
                return CelType.TypeParam(type.TypeParam);
            case global::Cel.Expr.Type.TypeKindOneofCase.Type_:
                return type.Type_.TypeKindCase == global::Cel.Expr.Type.TypeKindOneofCase.None
                    ? CelType.TypeType
                    : CelType.TypeOf(ToCelType(type.Type_));
            case global::Cel.Expr.Type.TypeKindOneofCase.AbstractType:
                return CelType.Opaque(type.AbstractType.Name, type.AbstractType.ParameterTypes.Select(ToCelType).ToArray());
            case global::Cel.Expr.Type.TypeKindOneofCase.Error:
                return CelType.Error;
            default:
                return CelType.Dyn;
        }
    }

    private static CelType Primitive(global::Cel.Expr.Type.Types.PrimitiveType p, bool nullable) => p switch
    {
        global::Cel.Expr.Type.Types.PrimitiveType.Bool => nullable ? CelType.BoolWrapper : CelType.Bool,
        global::Cel.Expr.Type.Types.PrimitiveType.Int64 => nullable ? CelType.IntWrapper : CelType.Int,
        global::Cel.Expr.Type.Types.PrimitiveType.Uint64 => nullable ? CelType.UintWrapper : CelType.Uint,
        global::Cel.Expr.Type.Types.PrimitiveType.Double => nullable ? CelType.DoubleWrapper : CelType.Double,
        global::Cel.Expr.Type.Types.PrimitiveType.String => nullable ? CelType.StringWrapper : CelType.String,
        global::Cel.Expr.Type.Types.PrimitiveType.Bytes => nullable ? CelType.BytesWrapper : CelType.Bytes,
        _ => CelType.Error,
    };

    // ---- values ----

    /// <summary>Hook for the Protobuf adaptation: converts a packed message into a CEL value.</summary>
    public static Func<Wkt.Any, CelValue>? AnyToValue { get; set; }

    public static CelValue ToCelValue(Value v)
    {
        switch (v.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                return Rt.NullValue.Instance;
            case Value.KindOneofCase.BoolValue:
                return Rt.BoolValue.Of(v.BoolValue);
            case Value.KindOneofCase.Int64Value:
                return IntValue.Of(v.Int64Value);
            case Value.KindOneofCase.Uint64Value:
                return UintValue.Of(v.Uint64Value);
            case Value.KindOneofCase.DoubleValue:
                return Rt.DoubleValue.Of(v.DoubleValue);
            case Value.KindOneofCase.StringValue:
                return Rt.StringValue.Of(v.StringValue);
            case Value.KindOneofCase.BytesValue:
                return new Rt.BytesValue(v.BytesValue.ToByteArray());
            case Value.KindOneofCase.EnumValue:
                return IntValue.Of(v.EnumValue.Value);
            case Value.KindOneofCase.ObjectValue:
                return AnyToValue != null ? AnyToValue(v.ObjectValue) : new ErrorValue("object values need the Protobuf adaptation");
            case Value.KindOneofCase.MapValue:
            {
                var entries = v.MapValue.Entries
                    .Select(e => new KeyValuePair<CelValue, CelValue>(ToCelValue(e.Key), ToCelValue(e.Value)))
                    .ToArray();
                return Rt.MapValue.Create(entries);
            }
            case Value.KindOneofCase.ListValue:
                return new Rt.ListValue(v.ListValue.Values.Select(ToCelValue).ToArray());
            case Value.KindOneofCase.TypeValue:
                return new TypeValue(v.TypeValue);
            default:
                return new ErrorValue("unsupported value kind " + v.KindCase);
        }
    }

    /// <summary>Hook for the Protobuf adaptation: proto equality of two message values.</summary>
    public static Func<CelValue, CelValue, bool?>? MessageEquals { get; set; }

    /// <summary>Exact equality: same runtime type and value; NaN equals NaN; maps are order-agnostic.</summary>
    public static bool StrictEquals(CelValue expected, CelValue actual)
    {
        switch (expected)
        {
            case Rt.DoubleValue d:
                return actual is Rt.DoubleValue a && (d.Value.Equals(a.Value) || (double.IsNaN(d.Value) && double.IsNaN(a.Value)));
            case IntValue i:
                return actual is IntValue ai && ai.Value == i.Value;
            case UintValue u:
                return actual is UintValue au && au.Value == u.Value;
            case Rt.ListValue l:
            {
                if (actual is not Rt.ListValue al || al.Count != l.Count) return false;
                for (int k = 0; k < l.Count; k++)
                {
                    if (!StrictEquals(l[k], al[k])) return false;
                }
                return true;
            }
            case Rt.MapValue m:
            {
                if (actual is not Rt.MapValue am || am.Count != m.Count) return false;
                foreach (var entry in m.Entries)
                {
                    bool found = false;
                    foreach (var other in am.Entries)
                    {
                        if (StrictEquals(entry.Key, other.Key))
                        {
                            found = StrictEquals(entry.Value, other.Value);
                            break;
                        }
                    }
                    if (!found) return false;
                }
                return true;
            }
            case MessageValue:
                return MessageEquals?.Invoke(expected, actual) ?? false;
            default:
                return expected.GetType() == actual.GetType() && expected.EqualsValue(actual);
        }
    }

    public static string Summarize(FileResult result)
    {
        var sb = new StringBuilder();
        sb.Append(result.File).Append(": ").Append(result.Passed).Append('/').Append(result.Total).Append(" passed");
        foreach (var f in result.Failures)
            sb.Append('\n').Append("  FAIL ").Append(f.Name).Append(": ").Append(f.Detail);
        return sb.ToString();
    }
}
