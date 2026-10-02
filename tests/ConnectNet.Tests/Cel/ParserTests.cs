using System.Linq;
using ConnectNet.Validation.Cel.Syntax;
using Xunit;

namespace ConnectNet.Tests.Cel;

public class ParserTests
{
    private static string Parse(string expr)
    {
        var result = Parser.Parse(expr);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        return result.Expr!.ToDebugString();
    }

    private static string ParseError(string expr)
    {
        var result = Parser.Parse(expr);
        Assert.False(result.IsSuccess, "expected a parse error for: " + expr);
        return result.Errors.FormatAll();
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("-1", "-1")]
    [InlineData("--1", "1")]
    [InlineData("---1", "-_(1)")]
    [InlineData("-a", "-_(a)")]
    [InlineData("--a", "a")]
    [InlineData("!true", "!_(true)")]
    [InlineData("!!true", "true")]
    [InlineData("!-1", "!_(-1)")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("0x10", "16")]
    [InlineData("-0x10", "-16")]
    [InlineData("0xFFu", "255u")]
    [InlineData("42u", "42u")]
    [InlineData("1.5", "1.5")]
    [InlineData(".5", "0.5")]
    [InlineData("-1e3", "-1000")]
    [InlineData("1E-3", "0.001")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    [InlineData("\"hi\"", "\"hi\"")]
    [InlineData("'hi'", "\"hi\"")]
    [InlineData("b'abc'", "b\"abc\"")]
    [InlineData("a.b.c", "a.b.c")]
    [InlineData(".a.b", ".a.b")]
    [InlineData("a[0]", "_[_](a, 0)")]
    [InlineData("a + b * c", "_+_(a, _*_(b, c))")]
    [InlineData("a - b - c", "_-_(_-_(a, b), c)")]
    [InlineData("a / b % c", "_%_(_/_(a, b), c)")]
    [InlineData("a < b == c", "_==_(_<_(a, b), c)")]
    [InlineData("a in b", "@in(a, b)")]
    [InlineData("a ? b : c ? d : e", "_?_:_(a, b, _?_:_(c, d, e))")]
    [InlineData("a || b || c", "_||_(_||_(a, b), c)")]
    [InlineData("a || b || c || d", "_||_(_||_(a, b), _||_(c, d))")]
    [InlineData("a && b || c", "_||_(_&&_(a, b), c)")]
    [InlineData("f(a, b)", "f(a, b)")]
    [InlineData("a.f(b)", "a.f(b)")]
    [InlineData("a.f()", "a.f()")]
    [InlineData("[1, 2,]", "[1, 2]")]
    [InlineData("[]", "[]")]
    [InlineData("{1: 2, 'a': b,}", "{1: 2, \"a\": b}")]
    [InlineData("{}", "{}")]
    [InlineData("Msg{}", "Msg{}")]
    [InlineData("pkg.Msg{a: 1, b: 2,}", "pkg.Msg{a: 1, b: 2}")]
    [InlineData(".pkg.Msg{a: 1}", ".pkg.Msg{a: 1}")]
    [InlineData("(a)", "a")]
    [InlineData("a.b[1].c(2)", "_[_](a.b, 1).c(2)")]
    [InlineData("1 -1", "_-_(1, 1)")]
    [InlineData("1 - -1", "_-_(1, -1)")]
    [InlineData("a // comment\n + b", "_+_(a, b)")]
    [InlineData("a.while", "a.while")]
    [InlineData("a.as()", "a.as()")]
    [InlineData("Msg{for: 1}", "Msg{for: 1}")]
    [InlineData("pkg.if.Msg{}", "pkg.if.Msg{}")]
    [InlineData("a.`b/c`", "a.b/c")]
    [InlineData("has(a.`content-type`)", "has(a.content-type)")]
    [InlineData("Msg{`in`: 1}", "Msg{in: 1}")]
    public void ParsesExpressions(string expr, string expected)
    {
        Assert.Equal(expected, Parse(expr));
    }

    [Theory]
    [InlineData("has(a.b)", "has(a.b)")]
    [InlineData("has(a.b.c)", "has(a.b.c)")]
    [InlineData("[1].all(x, x > 0)",
        "__comprehension__(x, [1], __result__, true, @not_strictly_false(__result__), _&&_(__result__, _>_(x, 0)), __result__)")]
    [InlineData("a.exists(x, x)",
        "__comprehension__(x, a, __result__, false, @not_strictly_false(!_(__result__)), _||_(__result__, x), __result__)")]
    [InlineData("a.exists_one(x, x)",
        "__comprehension__(x, a, __result__, 0, true, _?_:_(x, _+_(__result__, 1), __result__), _==_(__result__, 1))")]
    [InlineData("a.map(x, x * 2)",
        "__comprehension__(x, a, __result__, [], true, _+_(__result__, [_*_(x, 2)]), __result__)")]
    [InlineData("a.map(x, x > 1, x * 2)",
        "__comprehension__(x, a, __result__, [], true, _?_:_(_>_(x, 1), _+_(__result__, [_*_(x, 2)]), __result__), __result__)")]
    [InlineData("a.filter(x, x > 1)",
        "__comprehension__(x, a, __result__, [], true, _?_:_(_>_(x, 1), _+_(__result__, [x]), __result__), __result__)")]
    public void ExpandsMacros(string expr, string expected)
    {
        Assert.Equal(expected, Parse(expr));
    }

    [Fact]
    public void MacrosCanBeDisabled()
    {
        var result = Parser.Parse("has(a.b)", new ParserOptions { Macros = new Macro[0] });
        Assert.True(result.IsSuccess);
        Assert.Equal("has(a.b)", result.Expr!.ToDebugString());
        Assert.IsType<CallExpr>(result.Expr);
    }

    [Fact]
    public void RecordsMacroCalls()
    {
        var result = Parser.Parse("[1].all(x, x > 0)");
        var call = Assert.Single(result.SourceInfo.MacroCalls).Value;
        Assert.Equal("[1].all(x, _>_(x, 0))", call.ToDebugString());
    }

    [Theory]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("'\\x41\\x42'", "AB")]
    [InlineData("'\\u00e9'", "é")]
    [InlineData("'\\U0001F600'", "\U0001F600")]
    [InlineData("'\\101'", "A")]
    [InlineData("'\\377'", "ÿ")]
    [InlineData("'\\xFF'", "ÿ")]
    [InlineData("r'\\n'", "\\n")]
    [InlineData("R\"\\\\\"", "\\\\")]
    [InlineData("'''x''x'''", "x''x")]
    [InlineData("\"\"\"a\nb\"\"\"", "a\nb")]
    [InlineData("\"\"\"a\r\nb\"\"\"", "a\nb")]
    [InlineData("\"\"\"a\rb\"\"\"", "a\nb")]
    [InlineData("'\\a\\b\\f\\r\\t\\v\\\\\\?\\\"\\'\\`'", "\a\b\f\r\t\v\\?\"'`")]
    [InlineData("'\"'", "\"")]
    [InlineData("''", "")]
    public void DecodesStringLiterals(string expr, string expected)
    {
        var result = Parser.Parse(expr);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        var literal = Assert.IsType<LiteralExpr>(result.Expr);
        Assert.Equal(LiteralKind.String, literal.LiteralKind);
        Assert.Equal(expected, (string)literal.Value!);
    }

    [Theory]
    [InlineData("b'abc'", new byte[] { 97, 98, 99 })]
    [InlineData("b'\\xff'", new byte[] { 255 })]
    [InlineData("b'\\377'", new byte[] { 255 })]
    [InlineData("b'ÿ'", new byte[] { 195, 191 })]
    [InlineData("b'\\303\\277'", new byte[] { 195, 191 })]
    [InlineData("br'\\n'", new byte[] { 92, 110 })]
    [InlineData("B\"\"", new byte[0])]
    public void DecodesBytesLiterals(string expr, byte[] expected)
    {
        var result = Parser.Parse(expr);
        Assert.True(result.IsSuccess, result.Errors.FormatAll());
        var literal = Assert.IsType<LiteralExpr>(result.Expr);
        Assert.Equal(LiteralKind.Bytes, literal.LiteralKind);
        Assert.Equal(expected, (byte[])literal.Value!);
    }

    [Theory]
    [InlineData("9223372036854775808", "invalid int literal")]
    [InlineData("-9223372036854775809", "invalid int literal")]
    [InlineData("18446744073709551616u", "invalid uint literal")]
    [InlineData("-1u", "invalid uint literal")]
    [InlineData("1e400", "invalid double literal")]
    [InlineData("'\\s'", "unable to unescape string")]
    [InlineData("'\\uD83D'", "invalid unicode code point")]
    [InlineData("'\\UD83DDE03'", "invalid unicode code point")]
    [InlineData("'\\U00110000'", "invalid unicode code point")]
    [InlineData("b'\\u00ff'", "unable to unescape string")]
    [InlineData("'abc", "unterminated string literal")]
    [InlineData("'a\nb'", "unterminated string literal")]
    [InlineData("'\\8'", "unable to unescape string")]
    [InlineData("a +", "mismatched input")]
    [InlineData("a b", "mismatched input 'b'")]
    [InlineData("(a", "expecting ')'")]
    [InlineData("[1, 2", "expecting ']'")]
    [InlineData("f(1,)", "mismatched input")]
    [InlineData("a.in", "expecting IDENTIFIER")]
    [InlineData("in", "mismatched input 'in'")]
    [InlineData("as", "reserved identifier: as")]
    [InlineData("while(1)", "reserved identifier: while")]
    [InlineData("a = b", "token recognition error at: '='")]
    [InlineData("a & b", "token recognition error at: '&'")]
    [InlineData("a | b", "token recognition error at: '|'")]
    [InlineData("has(a)", "invalid argument to has() macro")]
    [InlineData("[].all(1, true)", "argument must be a simple name")]
    [InlineData("[].map(a.b, true)", "argument is not an identifier")]
    [InlineData("[].all(__result__, true)", "iteration variable overwrites accumulator variable")]
    [InlineData("a.?b", "mismatched input '?'")]
    [InlineData("[?a]", "mismatched input '?'")]
    [InlineData("a.b(){}", "mismatched input '{'")]
    [InlineData("é", "token recognition error")]
    public void ReportsErrors(string expr, string expectedFragment)
    {
        var errors = ParseError(expr);
        Assert.Contains(expectedFragment, errors);
    }

    [Fact]
    public void ErrorsCarryLineAndColumn()
    {
        var errors = ParseError("a +\n b +");
        Assert.StartsWith("ERROR: <input>:2:5: Syntax error: mismatched input '<EOF>'", errors);
        Assert.Contains("\n |  b +\n | ....^", errors);
    }

    [Fact]
    public void SupportsTheSpecifiedNestingAndRepetition()
    {
        Parse("a[a[a[a[a[a[a[a[a[a[a[a[0]]]]]]]]]]]]");
        Parse("f(f(f(f(f(f(f(f(f(f(f(f(0))))))))))))");
        Parse("[[[[[[[[[[[[0]]]]]]]]]]]]");
        Parse("{0: {0: {0: {0: {0: {0: {0: {0: {0: {0: {0: {0: 0}}}}}}}}}}}}");
        Parse("((((((((((((0))))))))))))");
        Parse(string.Join(" || ", Enumerable.Repeat("a", 32)));
        Parse(string.Join(" + ", Enumerable.Repeat("a", 24)));
        Parse(string.Join(" ? b : ", Enumerable.Repeat("a", 24)) + " ? b : c");
        Parse(string.Concat(Enumerable.Repeat("a.", 12)) + "b");
    }

    [Fact]
    public void RejectsExcessiveNesting()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 300)) + "0" + string.Concat(Enumerable.Repeat("]", 300));
        var errors = ParseError(deep);
        Assert.Contains("expression recursion limit exceeded", errors);

        var chain = string.Join(" + ", Enumerable.Repeat("a", 300));
        errors = ParseError(chain);
        Assert.Contains("expression recursion limit exceeded", errors);

        var ok = Parser.Parse(deep, new ParserOptions { MaxRecursionDepth = 400 });
        Assert.True(ok.IsSuccess, ok.Errors.FormatAll());
    }

    [Fact]
    public void RejectsOversizedSource()
    {
        var text = new string(' ', 20) + "1";
        var result = Parser.Parse(text, new ParserOptions { MaxExpressionSize = 10 });
        Assert.False(result.IsSuccess);
        Assert.Contains("expression code point size exceeds limit", result.Errors.FormatAll());
    }

    [Fact]
    public void PositionsCountCodePoints()
    {
        var result = Parser.Parse("'\U0001F600' + a");
        Assert.True(result.IsSuccess);
        var call = Assert.IsType<CallExpr>(result.Expr);
        Assert.Equal(4, result.SourceInfo.GetPosition(call.Id));
        Assert.Equal(6, result.SourceInfo.GetPosition(call.Args[1].Id));
    }

    [Fact]
    public void IdsAreUnique()
    {
        var result = Parser.Parse("[1, 2].map(x, x + 1).filter(y, y > 1)[0]");
        var ids = new System.Collections.Generic.HashSet<long>();
        void Visit(Expr e)
        {
            Assert.True(ids.Add(e.Id), "duplicate id " + e.Id);
            switch (e)
            {
                case SelectExpr s: Visit(s.Operand); break;
                case CallExpr c:
                    if (c.Target != null) Visit(c.Target);
                    foreach (var a in c.Args) Visit(a);
                    break;
                case ListExpr l: foreach (var x in l.Elements) Visit(x); break;
                case MapExpr m: foreach (var x in m.Entries) { Visit(x.Key); Visit(x.Value); } break;
                case StructExpr st: foreach (var x in st.Entries) Visit(x.Value); break;
                case ComprehensionExpr comp:
                    Visit(comp.IterRange); Visit(comp.AccuInit); Visit(comp.LoopCondition); Visit(comp.LoopStep); Visit(comp.Result);
                    break;
            }
        }
        Visit(result.Expr!);
    }
}
