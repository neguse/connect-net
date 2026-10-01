using System.Collections.Generic;
using System.Linq;
using ConnectNet.Validation.Cel;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Protovalidate;
using ConnectNet.Validation.Cel.Runtime;
using Xunit;

namespace ConnectNet.Tests.Cel;

/// <summary>
/// The Protovalidate function library on top of the CEL standard library. The string
/// predicates are pinned against the reference conformance suite's own cases (generated into
/// <see cref="ProtovalidateFunctionCases"/>), so <c>isEmail()</c> and friends accept exactly
/// what protovalidate-go accepts.
/// </summary>
public class ProtovalidateFunctionTests
{
    private static CelEnvironment NewEnv()
    {
        var env = new CelEnvironment()
            .AddStandardLibrary()
            .AddStringsExtension()
            .AddVariable("val", CelType.String)
            .AddVariable("m", CelType.Map(CelType.String, CelType.Int));
        return ProtovalidateFunctions.AddTo(env);
    }

    private static CelValue Eval(string expr, string val = "")
    {
        var env = NewEnv();
        return env.Compile(expr).Evaluate(Activation.Of(new Dictionary<string, CelValue>
        {
            ["val"] = StringValue.Of(val),
            ["now"] = new TimestampValue(1_700_000_000, 0),
            ["m"] = (MapValue)MapValue.Create(new[]
            {
                new KeyValuePair<CelValue, CelValue>(StringValue.Of("a"), IntValue.Of(1)),
            }),
        }));
    }

    private static bool EvalBool(string expr, string val = "")
    {
        var result = Eval(expr, val);
        var b = Assert.IsType<BoolValue>(result);
        return b.Value;
    }

    public static IEnumerable<object[]> LibraryCases() =>
        ProtovalidateFunctionCases.Library.Select(c => new object[] { c.Name, c.Expression, c.Value, c.Expected });

    [Theory]
    [MemberData(nameof(LibraryCases))]
    public void StringPredicates_MatchTheReferenceSuite(string name, string expression, string value, bool expected)
    {
        Assert.True(expected == EvalBool(expression, value), $"{name}: {expression} on {value} expected {expected}");
    }

    [Fact]
    public void ReferenceSuite_IsComplete()
    {
        // The pinned Protovalidate revision ships 635 library cases across the seven files.
        Assert.Equal(635, ProtovalidateFunctionCases.Library.Length);
    }

    [Theory]
    [InlineData("[1, 2, 3].unique()", true)]
    [InlineData("[1, 2, 1].unique()", false)]
    [InlineData("[].unique()", true)]
    [InlineData("['a', 'b'].unique()", true)]
    [InlineData("['a', 'a'].unique()", false)]
    [InlineData("[1.0, 1.0].unique()", false)]
    [InlineData("[1.0, 2.0].unique()", true)]
    [InlineData("[true, false].unique()", true)]
    [InlineData("[true, true].unique()", false)]
    [InlineData("[1u, 1u].unique()", false)]
    [InlineData("[b'a', b'a'].unique()", false)]
    [InlineData("[b'a', b'b'].unique()", true)]
    public void Unique_ComparesByValue(string expr, bool expected)
    {
        Assert.Equal(expected, EvalBool(expr));
    }

    [Theory]
    [InlineData("(0.0 / 0.0).isNan()", true)]
    [InlineData("(1.0).isNan()", false)]
    [InlineData("(1.0 / 0.0).isInf()", true)]
    [InlineData("(-1.0 / 0.0).isInf()", true)]
    [InlineData("(1.0).isInf()", false)]
    [InlineData("(1.0 / 0.0).isInf(1)", true)]
    [InlineData("(1.0 / 0.0).isInf(-1)", false)]
    [InlineData("(-1.0 / 0.0).isInf(-1)", true)]
    [InlineData("(-1.0 / 0.0).isInf(1)", false)]
    [InlineData("(-1.0 / 0.0).isInf(0)", true)]
    [InlineData("(1.0 / 0.0).isInf(0)", true)]
    public void IsNanAndIsInf(string expr, bool expected)
    {
        Assert.Equal(expected, EvalBool(expr));
    }

    [Theory]
    [InlineData("b'abc'.contains(b'b')", true)]
    [InlineData("b'abc'.contains(b'd')", false)]
    [InlineData("b'abc'.startsWith(b'ab')", true)]
    [InlineData("b'abc'.startsWith(b'bc')", false)]
    [InlineData("b'abc'.endsWith(b'bc')", true)]
    [InlineData("b'abc'.endsWith(b'ab')", false)]
    [InlineData("b''.contains(b'')", true)]
    // The string overloads keep working next to the bytes ones.
    [InlineData("'abc'.contains('b')", true)]
    [InlineData("'abc'.startsWith('ab')", true)]
    [InlineData("'abc'.endsWith('bc')", true)]
    [InlineData("'abc'.endsWith('ab')", false)]
    public void BytesOverloads_OfStringTests(string expr, bool expected)
    {
        Assert.Equal(expected, EvalBool(expr));
    }

    [Fact]
    public void MixedStringAndBytesTest_IsATypeError()
    {
        Assert.Throws<CelCompilationException>(() => NewEnv().Compile("'abc'.contains(b'b')"));
    }

    [Fact]
    public void GetField_ReadsAMapEntry()
    {
        Assert.Equal(1L, Assert.IsType<IntValue>(Eval("getField(m, 'a')")).Value);
        Assert.IsType<ErrorValue>(Eval("getField(m, 'missing')"));
    }

    [Fact]
    public void Now_IsADeclaredTimestamp()
    {
        Assert.True(EvalBool("now < timestamp('2100-01-01T00:00:00Z')"));
        Assert.True(EvalBool("now == timestamp(1700000000)"));
    }

    [Fact]
    public void StringPredicates_RequireAString()
    {
        Assert.Throws<CelCompilationException>(() => NewEnv().Compile("1.isEmail()"));
        Assert.Throws<CelCompilationException>(() => NewEnv().Compile("'a'.isIp('4')"));
        Assert.Throws<CelCompilationException>(() => NewEnv().Compile("'a'.isHostAndPort()"));
    }

    [Theory]
    [InlineData("'::1'.isIp(4)", false)]
    [InlineData("'::1'.isIp(6)", true)]
    public void IsIp_Versions(string expr, bool expected)
    {
        Assert.Equal(expected, EvalBool(expr));
    }
}
