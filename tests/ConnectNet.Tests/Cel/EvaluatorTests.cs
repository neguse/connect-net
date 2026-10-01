using System;
using System.Collections.Generic;
using System.Threading;
using ConnectNet.Validation.Cel;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Runtime;
using ConnectNet.Validation.Cel.Syntax;
using Xunit;

namespace ConnectNet.Tests.Cel;

public class EvaluatorTests
{
    private static CelEnvironment NewEnv() => new CelEnvironment()
        .AddStandardLibrary()
        .AddStringsExtension()
        .AddVariable("x", CelType.Int)
        .AddVariable("s", CelType.String)
        .AddVariable("ints", CelType.List(CelType.Int))
        .AddVariable("m", CelType.Map(CelType.String, CelType.Int))
        .AddVariable("d", CelType.Dyn);

    private static readonly Activation Bindings = Activation.Of(new Dictionary<string, CelValue>
    {
        ["x"] = IntValue.Of(42),
        ["s"] = StringValue.Of("hello"),
        ["ints"] = new ListValue(new CelValue[] { IntValue.Of(1), IntValue.Of(2), IntValue.Of(3) }),
        ["m"] = (MapValue)MapValue.Create(new[]
        {
            new KeyValuePair<CelValue, CelValue>(StringValue.Of("a"), IntValue.Of(1)),
            new KeyValuePair<CelValue, CelValue>(StringValue.Of("b"), IntValue.Of(2)),
        }),
        ["d"] = DoubleValue.Of(1.5),
    });

    /// <summary>Evaluates without the checker so that the runtime's dynamic semantics are exercised.</summary>
    private static CelValue Eval(string expr)
    {
        var env = NewEnv();
        var parsed = env.Parse(expr);
        Assert.True(parsed.IsSuccess, parsed.Errors.FormatAll());
        return env.PlanUnchecked(parsed).Evaluate(Bindings);
    }

    private static CelValue EvalChecked(string expr) => NewEnv().Compile(expr).Evaluate(Bindings);

    private static string EvalString(string expr) => Eval(expr).ToString();

    [Theory]
    [InlineData("1 + 2", "3")]
    [InlineData("x * 2", "84")]
    [InlineData("7 / 2", "3")]
    [InlineData("-7 / 2", "-3")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("7u % 3u", "1u")]
    [InlineData("1.0 / 0.0", "+Inf")]
    [InlineData("-(x)", "-42")]
    [InlineData("'a' + 'b'", "\"ab\"")]
    [InlineData("b'a' + b'b'", "b\"ab\"")]
    [InlineData("[1] + [2, 3]", "[1, 2, 3]")]
    [InlineData("1 < 2", "true")]
    [InlineData("1 < 1.5", "true")]
    [InlineData("2u > 1", "true")]
    [InlineData("1 == 1.0", "true")]
    [InlineData("dyn(1) == 1u", "true")]
    [InlineData("0.0/0.0 == 0.0/0.0", "false")]
    [InlineData("'a' < 'b'", "true")]
    [InlineData("'\U0001F600' > 'z'", "true")]
    [InlineData("b'a' < b'b'", "true")]
    [InlineData("[1, 2] == [1, 2]", "true")]
    [InlineData("{'a': 1} == {'a': 1.0}", "true")]
    [InlineData("{1: 'a'} == {1u: 'a'}", "true")]
    [InlineData("1 in [1, 2]", "true")]
    [InlineData("3.0 in [1, 2, 3]", "true")]
    [InlineData("'a' in m", "true")]
    [InlineData("2u in {2: 'x'}", "true")]
    [InlineData("ints[1]", "2")]
    [InlineData("ints[1.0]", "2")]
    [InlineData("ints[1u]", "2")]
    [InlineData("m['b']", "2")]
    [InlineData("m.a", "1")]
    [InlineData("{1: 'a', 2: 'b'}[2.0]", "\"b\"")]
    [InlineData("size('héllo')", "5")]
    [InlineData("size(b'héllo')", "6")]
    [InlineData("size(ints)", "3")]
    [InlineData("m.size()", "2")]
    [InlineData("true ? 1 : 2", "1")]
    [InlineData("false ? 1 : 2", "2")]
    [InlineData("true && false", "false")]
    [InlineData("false || true", "true")]
    [InlineData("!true", "false")]
    [InlineData("has(m.a)", "true")]
    [InlineData("has(m.z)", "false")]
    [InlineData("ints.all(i, i > 0)", "true")]
    [InlineData("ints.exists(i, i == 2)", "true")]
    [InlineData("ints.exists_one(i, i > 1)", "false")]
    [InlineData("ints.map(i, i * 2)", "[2, 4, 6]")]
    [InlineData("ints.map(i, i > 1, i * 2)", "[4, 6]")]
    [InlineData("ints.filter(i, i != 2)", "[1, 3]")]
    [InlineData("m.map(k, k)", "[\"a\", \"b\"]")]
    [InlineData("ints.map(i, ints.map(j, i * j))", "[[1, 2, 3], [2, 4, 6], [3, 6, 9]]")]
    [InlineData("int('12')", "12")]
    [InlineData("int(2.9)", "2")]
    [InlineData("int(-2.9)", "-2")]
    [InlineData("uint('12')", "12u")]
    [InlineData("double('1.5')", "1.5")]
    [InlineData("double('Infinity')", "+Inf")]
    [InlineData("string(1.5)", "\"1.5\"")]
    [InlineData("string(1e21)", "\"1e+21\"")]
    [InlineData("string(1e20)", "\"100000000000000000000\"")]
    [InlineData("string(0.00001)", "\"1e-05\"")]
    [InlineData("string(-4.5e-3)", "\"-0.0045\"")]
    [InlineData("string(123u)", "\"123\"")]
    [InlineData("string(b'\\303\\277')", "\"ÿ\"")]
    [InlineData("bool('TRUE')", "true")]
    [InlineData("bytes('a')", "b\"a\"")]
    [InlineData("type(1)", "int")]
    [InlineData("type(1u)", "uint")]
    [InlineData("type('a') == string", "true")]
    [InlineData("type(type(1)) == type(string)", "true")]
    [InlineData("type(timestamp(0)) == google.protobuf.Timestamp", "true")]
    [InlineData("type(ints)", "list")]
    [InlineData("type(null)", "null_type")]
    [InlineData("timestamp('2009-02-13T23:31:30Z')", "timestamp(\"2009-02-13T23:31:30Z\")")]
    [InlineData("string(timestamp('2009-02-13T23:31:30.5+01:00'))", "\"2009-02-13T22:31:30.5Z\"")]
    [InlineData("string(timestamp('9999-12-31T23:59:59.999999999Z'))", "\"9999-12-31T23:59:59.999999999Z\"")]
    [InlineData("int(timestamp('2009-02-13T23:31:30Z'))", "1234567890")]
    [InlineData("timestamp(1234567890).getFullYear()", "2009")]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getMonth()", "1")]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDayOfWeek()", "5")]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDate('Australia/Sydney')", "14")]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDayOfMonth('+11:00')", "13")]
    [InlineData("timestamp('2009-02-13T02:00:00Z').getDayOfMonth('-02:30')", "11")]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getHours('02:00')", "1")]
    [InlineData("timestamp('2009-02-13T23:31:20.123456789Z').getMilliseconds()", "123")]
    [InlineData("duration('1h30m')", "duration(\"5400s\")")]
    [InlineData("string(duration('1m1ms'))", "\"60.001s\"")]
    [InlineData("duration('1.5s').getMilliseconds()", "500")]
    [InlineData("duration('10000s').getHours()", "2")]
    [InlineData("timestamp('2009-02-13T23:00:00Z') + duration('240s') == timestamp('2009-02-13T23:04:00Z')", "true")]
    [InlineData("timestamp('2009-02-13T23:31:00Z') - timestamp('2009-02-13T23:29:00Z')", "duration(\"120s\")")]
    [InlineData("'foobar'.startsWith('foo') && 'foobar'.endsWith('bar') && 'foobar'.contains('ob')", "true")]
    [InlineData("'mañana'.matches('a+ñ+a+')", "true")]
    [InlineData("matches('abc', '^a')", "true")]
    [InlineData("'tacocat'.charAt(3)", "\"o\"")]
    [InlineData("'tacocat'.indexOf('c')", "2")]
    [InlineData("'tacocat'.lastIndexOf('c')", "4")]
    [InlineData("'TacoCat'.lowerAscii()", "\"tacocat\"")]
    [InlineData("'héllo'.upperAscii()", "\"HéLLO\"")]
    [InlineData("'hello hello'.replace('he', 'we')", "\"wello wello\"")]
    [InlineData("'hello hello'.replace('he', 'we', 1)", "\"wello hello\"")]
    [InlineData("'a,b,c'.split(',')", "[\"a\", \"b\", \"c\"]")]
    [InlineData("'a,b,c'.split(',', 2)", "[\"a\", \"b,c\"]")]
    [InlineData("'tacocat'.substring(4)", "\"cat\"")]
    [InlineData("'tacocat'.substring(0, 4)", "\"taco\"")]
    [InlineData("'  trim  '.trim()", "\"trim\"")]
    [InlineData("['a', 'b'].join()", "\"ab\"")]
    [InlineData("['a', 'b'].join('-')", "\"a-b\"")]
    [InlineData("'héllo'.reverse()", "\"olléh\"")]
    [InlineData("'%s %d %.2f %e %b %x %X %o'.format(['a', 1, 1.005, 1052.03, 5, 255, 255, 8])", "\"a 1 1.00 1.052030e+03 101 ff FF 10\"")]
    [InlineData("'%s'.format([[1, 'a', 2.5, null, duration('1h')]])", "\"[1, a, 2.5, null, 3600s]\"")]
    [InlineData("'%s'.format([{'b': 1, 'a': true}])", "\"{a: true, b: 1}\"")]
    [InlineData("'%f %e %d'.format([double('Infinity'), double('NaN'), double('-Infinity')])", "\"Infinity NaN -Infinity\"")]
    [InlineData("'%.0f %.0f'.format([1.5, 2.5])", "\"2 2\"")]
    [InlineData("'%%'.format([])", "\"%\"")]
    public void Evaluates(string expr, string expected)
    {
        var value = Eval(expr);
        Assert.False(value.IsError, value.ToString());
        Assert.Equal(expected, value.ToString());
    }

    [Theory]
    [InlineData("9223372036854775807 + 1", "integer overflow")]
    [InlineData("-9223372036854775808 - 1", "integer overflow")]
    [InlineData("-(-9223372036854775808)", "integer overflow")]
    [InlineData("5000000000 * 5000000000", "integer overflow")]
    [InlineData("(-9223372036854775808) / -1", "integer overflow")]
    [InlineData("0u - 1u", "unsigned integer overflow")]
    [InlineData("1 / 0", "divide by zero")]
    [InlineData("1 % 0", "modulus by zero")]
    [InlineData("int(1e99)", "integer overflow")]
    [InlineData("int(9223372036854775807.0)", "integer overflow")]
    [InlineData("uint(-1)", "unsigned integer overflow")]
    [InlineData("int(18446744073709551615u)", "integer overflow")]
    [InlineData("int('abc')", "type conversion error")]
    [InlineData("double('1e400')", "type conversion error")]
    [InlineData("string(b'\\000\\xff')", "invalid UTF-8")]
    [InlineData("timestamp('0000-01-01T00:00:00Z')", "timestamp overflow")]
    [InlineData("timestamp('10000-01-01T00:00:00Z')", "invalid RFC 3339")]
    [InlineData("timestamp(253402300800)", "timestamp overflow")]
    [InlineData("timestamp('9999-12-31T23:59:59.999999999Z') + duration('1ns')", "timestamp overflow")]
    [InlineData("duration('-320000000000s')", "invalid duration")]
    [InlineData("duration('1d')", "invalid duration")]
    [InlineData("ints[3]", "out of range")]
    [InlineData("ints[0.5]", "unsupported index value")]
    [InlineData("ints['a']", "unsupported index type")]
    [InlineData("m['z']", "no such key")]
    [InlineData("{1: 2}[1.5]", "no such key")]
    [InlineData("{1: 2, 1u: 3}", "repeated key")]
    [InlineData("{1.5: 2}", "unsupported map key type")]
    [InlineData("{null: 2}", "unsupported map key type")]
    [InlineData("dyn(1) + 'a'", "no such overload")]
    [InlineData("dyn('a') < 1", "no such overload")]
    [InlineData("0.0/0.0 < 1.0", "NaN values cannot be ordered")]
    [InlineData("dyn(1) ? 1 : 2", "no such overload")]
    [InlineData("dyn(1) && true", "no such overload")]
    [InlineData("1 / 0 > 1 && true", "divide by zero")]
    [InlineData("!dyn(1)", "no such overload")]
    [InlineData("dyn(1).foo", "no such overload")]
    [InlineData("has(dyn(1).foo)", "no such overload")]
    [InlineData("dyn(1).all(i, true)", "no such overload")]
    [InlineData("'tacocat'.charAt(30)", "index out of range: 30")]
    [InlineData("'tacocat'.substring(4, 3)", "invalid substring range")]
    [InlineData("'%d'.format(['a'])", "decimal clause can only be used on integers")]
    [InlineData("'%s %s'.format(['a'])", "index 1 out of range")]
    [InlineData("'%q'.format([1])", "unrecognized formatting clause")]
    [InlineData("timestamp(0).getHours('Nowhere/Land')", "unknown time zone")]
    public void ReportsErrors(string expr, string expectedFragment)
    {
        var value = Eval(expr);
        var error = Assert.IsType<ErrorValue>(value);
        Assert.Contains(expectedFragment, error.Message);
    }

    [Fact]
    public void QuotesStrings()
    {
        var value = Assert.IsType<StringValue>(Eval("strings.quote('a\"b\\n')"));
        Assert.Equal("\"a\\\"b\\n\"", value.Value);
    }

    [Fact]
    public void CheckedProgramsEvaluate()
    {
        Assert.Equal("43", EvalChecked("x + 1").ToString());
        Assert.Equal("[2, 4, 6]", EvalChecked("ints.map(i, i * 2)").ToString());
        Assert.Equal("false", EvalChecked("1 / 0 > 1 && false").ToString());
        Assert.Equal("true", EvalChecked("d == 1.5").ToString());
    }

    [Fact]
    public void LogicalOperatorsAbsorbErrors()
    {
        Assert.Equal("false", EvalString("1 / 0 > 1 && false"));
        Assert.Equal("false", EvalString("false && 1 / 0 > 1"));
        Assert.Equal("true", EvalString("1 / 0 > 1 || true"));
        Assert.Equal("true", EvalString("dyn(1) || true"));
        Assert.Equal("false", EvalString("dyn(1) && false"));
        Assert.Equal("true", EvalString("[1, 2, 0].exists(i, 1 / i > 0)"));
        Assert.IsType<ErrorValue>(Eval("[0, 1].all(i, 1 / i > 0)"));
        Assert.Equal("false", EvalString("[0, -1].all(i, 1 / i > 0)"));
        Assert.IsType<ErrorValue>(Eval("[0, 1].exists_one(i, 1 / i > 0)"));
        Assert.IsType<ErrorValue>(Eval("[0, 1].map(i, 1 / i)"));
        Assert.Equal("true", EvalString("[1, 0].all(i, i > 0 && 1 / i > 0) == false"));
    }

    [Fact]
    public void UncheckedExpressionsResolveNamesAtRuntime()
    {
        var env = new CelEnvironment(new Container("com.example")).AddStandardLibrary();
        var bindings = Activation.Of(new Dictionary<string, CelValue>
        {
            ["com.example.y"] = BoolValue.True,
            ["y"] = StringValue.Of("y"),
            ["y.z"] = IntValue.Of(42),
        });
        CelValue Run(string expr) => env.PlanUnchecked(env.Parse(expr)).Evaluate(bindings);

        Assert.Equal("true", Run("y").ToString());
        Assert.Equal("\"y\"", Run(".y").ToString());
        Assert.Equal("42", Run(".y.z").ToString());
        Assert.Equal("true", Run("[0].exists(y, y == 0)").ToString());
        Assert.Equal("true", Run("[{'z': 0}].exists(y, y.z == 0)").ToString());
        Assert.Equal("true", Run("['compre'].exists(y, .y == 'y')").ToString());
        Assert.Contains("no such attribute", ((ErrorValue)Run("nope")).Message);
        Assert.Contains("unbound function", ((ErrorValue)Run("nope(1)")).Message);
        Assert.Equal("[17, \"pancakes\"]", Run("[17, 'pancakes']").ToString());
        Assert.Equal("true", Run("1.0 == 1").ToString());
    }

    [Fact]
    public void CheckedExpressionsResolveNamesStatically()
    {
        var env = new CelEnvironment(new Container("com.example"))
            .AddStandardLibrary()
            .AddVariable("com.example.y", CelType.Bool)
            .AddVariable("y", CelType.String);
        var bindings = Activation.Of(new Dictionary<string, CelValue>
        {
            ["com.example.y"] = BoolValue.True,
            ["y"] = StringValue.Of("y"),
        });
        Assert.Equal("true", env.Compile("y").Evaluate(bindings).ToString());
        Assert.Equal("\"y\"", env.Compile(".y").Evaluate(bindings).ToString());
        Assert.Equal("true", env.Compile("[0].exists(y, y == 0)").Evaluate(bindings).ToString());
    }

    [Fact]
    public void TypeIdentifiersEvaluateToTypes()
    {
        Assert.Equal("int", EvalString("int"));
        Assert.Equal("list", EvalString("list"));
        Assert.Equal("google.protobuf.Duration", EvalString("google.protobuf.Duration"));
        Assert.Equal("true", EvalString("type(1) in [int, uint]"));
    }

    [Fact]
    public void BudgetBoundsEvaluation()
    {
        var program = NewEnv().Compile("ints.map(i, ints.map(j, ints.map(k, i + j + k)))");
        Assert.False(program.Evaluate(Bindings).IsError);
        Assert.Throws<CelEvaluationException>(() => program.Evaluate(Bindings, budget: 20));

        // Errors inside logical operators do not hide an exhausted budget.
        var absorbing = NewEnv().Compile("false || ints.map(i, ints.map(j, ints.map(k, i + j + k))).size() > 0");
        Assert.Throws<CelEvaluationException>(() => absorbing.Evaluate(Bindings, budget: 20));
    }

    [Fact]
    public void CancellationStopsEvaluation()
    {
        var program = NewEnv().Compile("ints.map(i, i)");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Assert.Throws<CelEvaluationException>(() => program.Evaluate(Bindings, cancellationToken: cts.Token));
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void CompilationErrorsThrow()
    {
        var ex = Assert.Throws<CelCompilationException>(() => NewEnv().Compile("x +"));
        Assert.Contains("Syntax error", ex.Message);
        ex = Assert.Throws<CelCompilationException>(() => NewEnv().Compile("x + 'a'"));
        Assert.Contains("found no matching overload", ex.Message);
    }

    [Fact]
    public void ProgramsAreReusableAcrossActivations()
    {
        var program = NewEnv().Compile("x + 1");
        Assert.Equal("43", program.Evaluate(Bindings).ToString());
        Assert.Equal("2", program.Evaluate(Activation.Of(new Dictionary<string, CelValue> { ["x"] = IntValue.Of(1) })).ToString());
        Assert.Contains("no such attribute", ((ErrorValue)program.Evaluate(Activation.Empty)).Message);
    }

    [Theory]
    [InlineData("1h", 3_600_000_000_000L)]
    [InlineData("-1.5h", -5_400_000_000_000L)]
    [InlineData("1h34us", 3_600_000_034_000L)]
    [InlineData("300ms", 300_000_000L)]
    [InlineData("1us", 1_000L)]
    [InlineData("1µs", 1_000L)]
    [InlineData("1ns", 1L)]
    [InlineData("0", 0L)]
    [InlineData("+2m3.5s", 123_500_000_000L)]
    [InlineData("9223372036854775807ns", long.MaxValue)]
    public void ParsesDurations(string text, long expected)
    {
        Assert.True(GoFormat.TryParseDuration(text, out var nanos));
        Assert.Equal(expected, nanos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1d")]
    [InlineData("h")]
    [InlineData("9223372036854775808ns")]
    public void RejectsBadDurations(string text)
    {
        Assert.False(GoFormat.TryParseDuration(text, out _));
    }
}
