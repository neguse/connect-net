using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Syntax;
using Xunit;

namespace ConnectNet.Tests.Cel;

public class CheckerTests
{
    /// <summary>A provider with one message, <c>test.Msg</c>, for field selection and construction.</summary>
    private sealed class TestProvider : TypeProvider
    {
        private static readonly Dictionary<string, Dictionary<string, CelType>> Messages = new()
        {
            ["test.Msg"] = new Dictionary<string, CelType>
            {
                ["name"] = CelType.String,
                ["count"] = CelType.Int,
                ["tags"] = CelType.List(CelType.String),
                ["attrs"] = CelType.Map(CelType.String, CelType.Int),
                ["child"] = CelType.Message("test.Msg"),
                ["wrapped"] = CelType.IntWrapper,
                ["ts"] = CelType.Timestamp,
                ["any"] = CelType.Any,
                ["value"] = CelType.Message("google.protobuf.Value"),
                ["color"] = CelType.Int,
            },
            ["test.Other"] = new Dictionary<string, CelType>(),
        };

        public override bool HasMessage(string fullName) => Messages.ContainsKey(fullName)
            || fullName.StartsWith("google.protobuf.");

        public override CelType? FindFieldType(string messageName, string fieldName)
        {
            if (messageName == "google.protobuf.Int64Value" && fieldName == "value") return CelType.Int;
            if (messageName == "google.protobuf.Timestamp" && fieldName == "seconds") return CelType.Int;
            return Messages.TryGetValue(messageName, out var fields) && fields.TryGetValue(fieldName, out var t) ? t : null;
        }

        public override bool TryFindEnumValue(string fullName, out long value)
        {
            if (fullName == "test.Color.RED") { value = 1; return true; }
            value = 0;
            return false;
        }
    }

    private static CheckerEnv NewEnv(string container = "")
    {
        return new CheckerEnv(new Container(container), new TestProvider())
            .AddStandardLibrary()
            .AddStringsExtension()
            .AddVariable("x", CelType.Int)
            .AddVariable("s", CelType.String)
            .AddVariable("d", CelType.Dyn)
            .AddVariable("msg", CelType.Message("test.Msg"))
            .AddVariable("test.pkgvar", CelType.String)
            .AddVariable("ints", CelType.List(CelType.Int))
            .AddVariable("m", CelType.Map(CelType.String, CelType.Int));
    }

    private static CheckResult Check(string expr, string container = "")
    {
        var parsed = Parser.Parse(expr);
        Assert.True(parsed.IsSuccess, parsed.Errors.FormatAll());
        return Checker.Check(parsed, NewEnv(container));
    }

    private static string TypeOf(string expr, string container = "")
    {
        var result = Check(expr, container);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        return result.ResultType.ToString();
    }

    private static string ErrorOf(string expr, string container = "")
    {
        var result = Check(expr, container);
        Assert.False(result.IsSuccess, "expected a check error for: " + expr);
        return result.Errors.FormatAll();
    }

    [Theory]
    [InlineData("1", "int")]
    [InlineData("1u", "uint")]
    [InlineData("1.0", "double")]
    [InlineData("'a'", "string")]
    [InlineData("b'a'", "bytes")]
    [InlineData("true", "bool")]
    [InlineData("null", "null")]
    [InlineData("x", "int")]
    [InlineData("x + 1", "int")]
    [InlineData("1.0 + 2.0", "double")]
    [InlineData("'a' + 'b'", "string")]
    [InlineData("-x", "int")]
    [InlineData("!true", "bool")]
    [InlineData("x < 1.0", "bool")]
    [InlineData("1u < x", "bool")]
    [InlineData("x == 1", "bool")]
    [InlineData("x != 1", "bool")]
    [InlineData("d == 1", "bool")]
    [InlineData("null == msg", "bool")]
    [InlineData("msg.wrapped == null", "bool")]
    [InlineData("true ? 1 : 2", "int")]
    [InlineData("true ? 1 : d", "dyn")]
    [InlineData("true && false || true", "bool")]
    [InlineData("[1, 2]", "list(int)")]
    [InlineData("[]", "list(dyn)")]
    [InlineData("[1, 'a']", "list(dyn)")]
    [InlineData("[1, d]", "list(dyn)")]
    [InlineData("{'a': 1}", "map(string, int)")]
    [InlineData("{}", "map(dyn, dyn)")]
    [InlineData("{1: 'a', 'b': 2}", "map(dyn, dyn)")]
    [InlineData("[1, 2][0]", "int")]
    [InlineData("{'a': 1}['a']", "int")]
    [InlineData("m['k']", "int")]
    [InlineData("m.k", "int")]
    [InlineData("d.anything", "dyn")]
    [InlineData("d[0]", "dyn")]
    [InlineData("1 in [1, 2]", "bool")]
    [InlineData("'k' in m", "bool")]
    [InlineData("size('abc')", "int")]
    [InlineData("'abc'.size()", "int")]
    [InlineData("size(ints)", "int")]
    [InlineData("size(m)", "int")]
    [InlineData("size(d)", "int")]
    [InlineData("int('1')", "int")]
    [InlineData("string(1)", "string")]
    [InlineData("double(1u)", "double")]
    [InlineData("bytes('a')", "bytes")]
    [InlineData("dyn(1)", "dyn")]
    [InlineData("m.`k`", "int")]
    [InlineData("type(1)", "type(int)")]
    [InlineData("type(d)", "type(dyn)")]
    [InlineData("type(1) == int", "bool")]
    [InlineData("int", "type(int)")]
    [InlineData("list", "type(list(dyn))")]
    [InlineData("map", "type(map(dyn, dyn))")]
    [InlineData("google.protobuf.Timestamp", "type(timestamp)")]
    [InlineData("timestamp('2020-01-01T00:00:00Z')", "timestamp")]
    [InlineData("duration('1h')", "duration")]
    [InlineData("timestamp(0) + duration('1h')", "timestamp")]
    [InlineData("timestamp(0) - timestamp(0)", "duration")]
    [InlineData("timestamp(0).getFullYear()", "int")]
    [InlineData("timestamp(0).getHours('UTC')", "int")]
    [InlineData("duration('1h').getHours()", "int")]
    [InlineData("'abc'.contains('b')", "bool")]
    [InlineData("'abc'.matches('b')", "bool")]
    [InlineData("matches('abc', 'b')", "bool")]
    [InlineData("'abc'.startsWith('a') && 'abc'.endsWith('c')", "bool")]
    [InlineData("'a'.charAt(0)", "string")]
    [InlineData("'a'.indexOf('a', 0)", "int")]
    [InlineData("'a,b'.split(',')", "list(string)")]
    [InlineData("'a'.format([1])", "string")]
    [InlineData("['a'].join()", "string")]
    [InlineData("strings.quote('a')", "string")]
    [InlineData("'a'.reverse()", "string")]
    [InlineData("ints.all(i, i > 0)", "bool")]
    [InlineData("ints.exists(i, i > 0)", "bool")]
    [InlineData("ints.exists_one(i, i > 0)", "bool")]
    [InlineData("ints.map(i, i * 2)", "list(int)")]
    [InlineData("ints.map(i, i > 0, 'a')", "list(string)")]
    [InlineData("ints.filter(i, i > 0)", "list(int)")]
    [InlineData("m.all(k, k == 'a')", "bool")]
    [InlineData("m.map(k, m[k])", "list(int)")]
    [InlineData("d.all(k, k == 'a')", "bool")]
    [InlineData("d.map(k, k)", "list(dyn)")]
    [InlineData("[].map(k, k)", "list(dyn)")]
    [InlineData("has(msg.name)", "bool")]
    [InlineData("has(d.x)", "bool")]
    [InlineData("has(m.k)", "bool")]
    [InlineData("msg.name", "string")]
    [InlineData("msg.child.child.count", "int")]
    [InlineData("msg.tags[0]", "string")]
    [InlineData("msg.attrs['a']", "int")]
    [InlineData("msg.wrapped", "wrapper(int)")]
    [InlineData("msg.wrapped + 1", "int")]
    [InlineData("msg.ts", "timestamp")]
    [InlineData("msg.any", "any")]
    [InlineData("msg.any.foo", "dyn")]
    [InlineData("msg.value", "dyn")]
    [InlineData("test.Msg{name: 'a', count: 1}", "test.Msg")]
    [InlineData("test.Msg{}", "test.Msg")]
    [InlineData("test.Msg{wrapped: null}", "test.Msg")]
    [InlineData("test.Msg{wrapped: 1}", "test.Msg")]
    [InlineData("test.Msg{child: null}", "test.Msg")]
    [InlineData("google.protobuf.Int64Value{value: 1}", "wrapper(int)")]
    [InlineData("google.protobuf.Timestamp{seconds: 1}", "timestamp")]
    [InlineData("test.Color.RED", "int")]
    [InlineData("test.Color.RED == msg.color", "bool")]
    [InlineData("test.pkgvar", "string")]
    [InlineData("[1] + [2]", "list(int)")]
    [InlineData("[1] + [d]", "list(dyn)")]
    [InlineData("[1] == [1]", "bool")]
    [InlineData("{'a': 1} == {'a': 2}", "bool")]
    [InlineData("1 == 1 ? 'a' : 'b'", "string")]
    public void InfersTypes(string expr, string expected)
    {
        Assert.Equal(expected, TypeOf(expr));
    }

    [Theory]
    [InlineData("pkgvar", "string", "test")]
    [InlineData("Msg{name: 'a'}", "test.Msg", "test")]
    [InlineData("Color.RED", "int", "test")]
    [InlineData("msg.name", "string", "test")]
    [InlineData("x", "int", "test.sub")]
    [InlineData(".x", "int", "test")]
    public void ResolvesNamesThroughTheContainer(string expr, string expected, string container)
    {
        Assert.Equal(expected, TypeOf(expr, container));
    }

    [Fact]
    public void RewritesQualifiedNames()
    {
        var result = Check("pkgvar + Msg{}.name", "test");
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        Assert.Equal("_+_(test.pkgvar, test.Msg{}.name)", result.Expr!.ToDebugString());

        result = Check("test.Color.RED");
        Assert.True(result.IsSuccess);
        var ident = Assert.IsType<IdentExpr>(result.Expr);
        Assert.Equal("test.Color.RED", ident.Name);
        Assert.Equal(1L, result.References[ident.Id].ConstantValue);
    }

    [Fact]
    public void LocalsShadowGlobalsAndDisambiguate()
    {
        var env = NewEnv().AddVariable("test.x", CelType.String);
        var parsed = Parser.Parse("ints.map(x, x + 1)");
        var result = Checker.Check(parsed, env);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        Assert.Equal("list(int)", result.ResultType.ToString());

        // Inside the comprehension `x` is the loop variable (int); `test.x` is still reachable
        // by its qualified name and a global `x` referenced from a container is marked absolute.
        parsed = Parser.Parse("ints.map(x, test.x)");
        result = Checker.Check(parsed, env);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        Assert.Equal("list(string)", result.ResultType.ToString());
    }

    [Fact]
    public void RecordsOverloadReferences()
    {
        var result = Check("x + 1");
        var call = Assert.IsType<CallExpr>(result.Expr);
        Assert.Equal(new[] { "add_int64" }, result.References[call.Id].OverloadIds);

        result = Check("d + 1");
        call = Assert.IsType<CallExpr>(result.Expr);
        Assert.Equal(new[] { "add_int64" }, result.References[call.Id].OverloadIds);

        result = Check("d + d");
        call = Assert.IsType<CallExpr>(result.Expr);
        Assert.Equal(9, result.References[call.Id].OverloadIds.Count);
        Assert.Equal("dyn", result.ResultType.ToString());

        result = Check("size(d)");
        call = Assert.IsType<CallExpr>(result.Expr);
        Assert.Equal(new[] { "size_bytes", "size_list", "size_map", "size_string" }, result.References[call.Id].OverloadIds);
    }

    [Fact]
    public void AssignsTypesToEveryNode()
    {
        var result = Check("ints.map(i, i * 2).filter(j, j > 2)[0] + x");
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        void Visit(Expr e)
        {
            Assert.True(result.Types.ContainsKey(e.Id), "missing type for " + e);
            switch (e)
            {
                case SelectExpr s: Visit(s.Operand); break;
                case CallExpr c:
                    if (c.Target != null) Visit(c.Target);
                    foreach (var a in c.Args) Visit(a);
                    break;
                case ListExpr l: foreach (var el in l.Elements) Visit(el); break;
                case ComprehensionExpr comp:
                    Visit(comp.IterRange); Visit(comp.AccuInit); Visit(comp.LoopCondition); Visit(comp.LoopStep); Visit(comp.Result);
                    break;
            }
        }
        Visit(result.Expr!);
        foreach (var t in result.Types.Values)
            Assert.NotEqual(TypeKind.TypeParam, t.Kind);
    }

    [Theory]
    [InlineData("y", "undeclared reference to 'y' (in container '')")]
    [InlineData("f(1)", "undeclared reference to 'f' (in container '')")]
    [InlineData("x.f()", "undeclared reference to 'f'")]
    [InlineData("1 + 'a'", "found no matching overload for '_+_' applied to '(int, string)'")]
    [InlineData("x < 'a'", "found no matching overload for '_<_' applied to '(int, string)'")]
    [InlineData("1 == 1.0", "found no matching overload for '_==_' applied to '(int, double)'")]
    [InlineData("x == null", "found no matching overload for '_==_' applied to '(int, null)'")]
    [InlineData("'a'.contains(1)", "found no matching overload for 'contains' applied to 'string.(int)'")]
    [InlineData("-'a'", "found no matching overload for '-_' applied to '(string)'")]
    [InlineData("1 && true", "expected type 'bool' but found 'int'")]
    [InlineData("true || 'a'", "expected type 'bool' but found 'string'")]
    [InlineData("1 ? 1 : 2", "found no matching overload for '_?_:_' applied to '(int, int, int)'")]
    [InlineData("true ? 1 : 'a'", "found no matching overload for '_?_:_' applied to '(bool, int, string)'")]
    [InlineData("x.foo", "type 'int' does not support field selection")]
    [InlineData("msg.nope", "undefined field 'nope'")]
    [InlineData("test.Msg{nope: 1}", "undefined field 'nope'")]
    [InlineData("test.Msg{name: 1}", "expected type of field 'name' is 'string' but provided type is 'int'")]
    [InlineData("test.Nope{}", "undeclared reference to 'test.Nope' (in container '')")]
    [InlineData("int{}", "'int' is not a message type")]
    [InlineData("x.all(i, true)", "expression of type 'int' cannot be range of a comprehension (must be list, map, or dynamic)")]
    [InlineData("ints.all(i, i)", "expected type 'bool' but found 'int'")]
    [InlineData("ints.map(i, i)[0] + 'a'", "found no matching overload for '_+_' applied to '(int, string)'")]
    [InlineData("[1, 2][x > 0]", "found no matching overload for '_[_]' applied to '(list(int), bool)'")]
    [InlineData("'a' in ints", "found no matching overload for '@in' applied to '(string, list(int))'")]
    [InlineData("has(x.y)", "type 'int' does not support field selection")]
    [InlineData("1.getFullYear()", "found no matching overload for 'getFullYear' applied to 'int.()'")]
    [InlineData("[1, 2].join()", "found no matching overload for 'join' applied to 'list(int).()'")]
    public void ReportsErrors(string expr, string expectedFragment)
    {
        Assert.Contains(expectedFragment, ErrorOf(expr));
    }

    [Fact]
    public void ErrorsCarryPositions()
    {
        var errors = ErrorOf("x + 'a'");
        Assert.StartsWith("ERROR: <input>:1:3: found no matching overload", errors);
    }

    [Fact]
    public void HomogeneousAggregateLiteralsCanBeRequired()
    {
        var env = new CheckerEnv(options: new CheckerOptions { HomogeneousAggregateLiterals = true }).AddStandardLibrary();
        var result = Checker.Check(Parser.Parse("[1, 'a']"), env);
        Assert.False(result.IsSuccess);
        Assert.Contains("expected type 'int' but found 'string'", result.Errors.FormatAll());
    }

    [Fact]
    public void CrossTypeNumericComparisonsCanBeDisabled()
    {
        var env = new CheckerEnv(options: new CheckerOptions { CrossTypeNumericComparisons = false }).AddStandardLibrary();
        var result = Checker.Check(Parser.Parse("1 < 1.0"), env);
        Assert.False(result.IsSuccess);
        Assert.Contains("found no matching overload for '_<_' applied to '(int, double)'", result.Errors.FormatAll());
        result = Checker.Check(Parser.Parse("1 < 2"), env);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void GenericOverloadsUnifyAcrossArguments()
    {
        // add_list binds A from the first list; the second must agree, dyn is fine.
        Assert.Equal("list(int)", TypeOf("[1] + ints"));
        Assert.Contains("found no matching overload for '_+_' applied to '(list(int), list(string))'", ErrorOf("[1] + ['a']"));
        // index_map binds the key type from the map, then checks the index.
        Assert.Contains("found no matching overload for '_[_]' applied to '(map(string, int), int)'", ErrorOf("m[1]"));
        Assert.Equal("int", TypeOf("m[d]"));
        // Nested comprehensions and accumulators keep their own scopes.
        Assert.Equal("list(list(int))", TypeOf("ints.map(i, ints.map(j, i * j))"));
    }
}
