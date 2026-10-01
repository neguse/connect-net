using System;
using System.Collections.Generic;
using System.Globalization;

namespace ConnectNet.Validation.Cel.Syntax;

internal sealed class ParserOptions
{
    /// <summary>cel-go's default; the specification requires implementations to support at least 12 nested levels.</summary>
    public const int DefaultMaxRecursionDepth = 250;

    /// <summary>Default cap on the expression length in code points.</summary>
    public const int DefaultMaxExpressionSize = 100_000;

    public static readonly ParserOptions Default = new();

    /// <summary>Maximum nesting depth of the resulting syntax tree, counted after macro expansion.</summary>
    public int MaxRecursionDepth { get; init; } = DefaultMaxRecursionDepth;

    /// <summary>Maximum source length in code points.</summary>
    public int MaxExpressionSize { get; init; } = DefaultMaxExpressionSize;

    /// <summary>Macros to expand; the standard set by default. Empty disables macro expansion.</summary>
    public IReadOnlyList<Macro> Macros { get; init; } = StandardMacros.All_;

    /// <summary>Record the original call of each expanded macro in <see cref="SourceInfo.MacroCalls"/>.</summary>
    public bool PopulateMacroCalls { get; init; } = true;
}

internal sealed class ParseResult
{
    public ParseResult(Expr? expr, SourceInfo sourceInfo, CelErrors errors)
    {
        Expr = expr;
        SourceInfo = sourceInfo;
        Errors = errors;
    }

    /// <summary>The parsed expression; null when <see cref="Errors"/> is non-empty.</summary>
    public Expr? Expr { get; }

    public SourceInfo SourceInfo { get; }

    public CelErrors Errors { get; }

    public bool IsSuccess => Expr != null && !Errors.HasErrors;
}

/// <summary>
/// Recursive-descent parser for the CEL grammar. Produces the same tree shapes as the
/// reference implementation (operators as calls with canonical names, macros expanded to
/// comprehensions) so that the checker and evaluator can follow the specification.
/// </summary>
internal sealed class Parser
{
    private sealed class SyntaxException : Exception
    {
        public SyntaxException(string message, int offset) : base(message)
        {
            Offset = offset;
        }

        public int Offset { get; }
    }

    private readonly ParserOptions _options;
    private readonly Dictionary<string, Macro> _macros;
    private readonly Source _source;
    private readonly CelErrors _errors;
    private readonly SourceInfo _sourceInfo;
    private List<Token> _tokens = new();
    private int _index;
    private long _nextId = 1;
    private int _depth;

    private Parser(Source source, ParserOptions options)
    {
        _source = source;
        _options = options;
        _errors = new CelErrors(source);
        _sourceInfo = new SourceInfo(source);
        _macros = new Dictionary<string, Macro>(StringComparer.Ordinal);
        foreach (var macro in options.Macros)
            _macros[macro.Key] = macro;
    }

    public static ParseResult Parse(string text, ParserOptions? options = null) =>
        Parse(new Source(text), options);

    public static ParseResult Parse(Source source, ParserOptions? options = null)
    {
        var parser = new Parser(source, options ?? ParserOptions.Default);
        return parser.Run();
    }

    internal long NextId(int offset)
    {
        var id = _nextId++;
        _sourceInfo.SetPosition(id, offset);
        return id;
    }

    private ParseResult Run()
    {
        if (_source.Length > _options.MaxExpressionSize)
        {
            _errors.Report(
                $"expression code point size exceeds limit: size: {_source.Length}, limit {_options.MaxExpressionSize}",
                -1);
            return new ParseResult(null, _sourceInfo, _errors);
        }

        Expr? expr = null;
        try
        {
            _tokens = new Lexer(_source).Tokenize();
            var last = _tokens[_tokens.Count - 1];
            if (last.Kind == TokenKind.Error)
                throw new SyntaxException(last.Text, last.Start);

            expr = ParseExpr();
            if (Current.Kind != TokenKind.Eof)
                throw new SyntaxException(Unexpected(Current), Current.Start);
        }
        catch (SyntaxException e)
        {
            _errors.Report("Syntax error: " + e.Message, e.Offset);
        }
        catch (MacroException e)
        {
            _errors.Report(e.Message, _sourceInfo.GetPosition(e.ExprId), e.ExprId);
        }

        if (expr != null && !_errors.HasErrors)
        {
            int depth = Depth(expr, 0);
            if (depth > _options.MaxRecursionDepth)
            {
                _errors.Report($"expression recursion limit exceeded: {_options.MaxRecursionDepth}", -1);
            }
        }

        return _errors.HasErrors
            ? new ParseResult(null, _sourceInfo, _errors)
            : new ParseResult(expr, _sourceInfo, _errors);
    }

    /// <summary>Nesting depth of the tree; a bound on how deep the checker and evaluator recurse.</summary>
    internal static int Depth(Expr expr, int current)
    {
        current++;
        switch (expr)
        {
            case SelectExpr s:
                return Depth(s.Operand, current);
            case CallExpr c:
            {
                int max = c.Target != null ? Depth(c.Target, current) : current;
                foreach (var a in c.Args)
                    max = Math.Max(max, Depth(a, current));
                return max;
            }
            case ListExpr l:
            {
                int max = current;
                foreach (var e in l.Elements)
                    max = Math.Max(max, Depth(e, current));
                return max;
            }
            case MapExpr m:
            {
                int max = current;
                foreach (var e in m.Entries)
                {
                    max = Math.Max(max, Depth(e.Key, current));
                    max = Math.Max(max, Depth(e.Value, current));
                }
                return max;
            }
            case StructExpr st:
            {
                int max = current;
                foreach (var e in st.Entries)
                    max = Math.Max(max, Depth(e.Value, current));
                return max;
            }
            case ComprehensionExpr comp:
            {
                int max = Depth(comp.IterRange, current);
                max = Math.Max(max, Depth(comp.AccuInit, current));
                max = Math.Max(max, Depth(comp.LoopCondition, current));
                max = Math.Max(max, Depth(comp.LoopStep, current));
                max = Math.Max(max, Depth(comp.Result, current));
                return max;
            }
            default:
                return current;
        }
    }

    private Token Current => _tokens[_index];

    private Token PeekToken(int ahead) =>
        _index + ahead < _tokens.Count ? _tokens[_index + ahead] : _tokens[_tokens.Count - 1];

    private Token Advance()
    {
        var tok = _tokens[_index];
        if (tok.Kind != TokenKind.Eof) _index++;
        return tok;
    }

    private bool Accept(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Current.Kind != kind)
            throw new SyntaxException($"{Unexpected(Current)}, expecting {what}", Current.Start);
        return Advance();
    }

    private string Unexpected(Token tok)
    {
        if (tok.Kind == TokenKind.Eof)
            return "mismatched input '<EOF>'";
        return $"mismatched input '{_source.Slice(tok.Start, tok.End)}'";
    }

    private void Enter()
    {
        _depth++;
        if (_depth > _options.MaxRecursionDepth)
            throw new SyntaxException($"expression recursion limit exceeded: {_options.MaxRecursionDepth}", Current.Start);
    }

    private void Exit() => _depth--;

    // Expr = ConditionalOr ["?" ConditionalOr ":" Expr]
    private Expr ParseExpr()
    {
        Enter();
        try
        {
            var condition = ParseConditionalOr();
            if (Current.Kind != TokenKind.Question)
                return condition;
            var op = Advance();
            var whenTrue = ParseConditionalOr();
            Expect(TokenKind.Colon, "':'");
            var whenFalse = ParseExpr();
            return new CallExpr(NextId(op.Start), Operators.Conditional, null, new[] { condition, whenTrue, whenFalse });
        }
        finally
        {
            Exit();
        }
    }

    private Expr ParseConditionalOr()
    {
        var first = ParseConditionalAnd();
        if (Current.Kind != TokenKind.OrOr)
            return first;
        var terms = new List<Expr> { first };
        var ops = new List<int>();
        while (Current.Kind == TokenKind.OrOr)
        {
            ops.Add(Advance().Start);
            terms.Add(ParseConditionalAnd());
        }
        return Balance(Operators.LogicalOr, terms, ops, 0, ops.Count - 1);
    }

    private Expr ParseConditionalAnd()
    {
        var first = ParseRelation();
        if (Current.Kind != TokenKind.AndAnd)
            return first;
        var terms = new List<Expr> { first };
        var ops = new List<int>();
        while (Current.Kind == TokenKind.AndAnd)
        {
            ops.Add(Advance().Start);
            terms.Add(ParseRelation());
        }
        return Balance(Operators.LogicalAnd, terms, ops, 0, ops.Count - 1);
    }

    /// <summary>
    /// Builds a balanced tree for a run of the same associative operator so that long
    /// <c>||</c> / <c>&amp;&amp;</c> chains do not nest linearly.
    /// </summary>
    private Expr Balance(string function, List<Expr> terms, List<int> ops, int lo, int hi)
    {
        // lo..hi index the operators; term i sits left of operator i.
        int mid = (lo + hi + 1) / 2;
        var left = mid == lo ? terms[mid] : Balance(function, terms, ops, lo, mid - 1);
        var right = mid == hi ? terms[mid + 1] : Balance(function, terms, ops, mid + 1, hi);
        return new CallExpr(NextId(ops[mid]), function, null, new[] { left, right });
    }

    private Expr ParseRelation()
    {
        var left = ParseAddition();
        while (true)
        {
            string? function = Current.Kind switch
            {
                TokenKind.Less => Operators.Less,
                TokenKind.LessEquals => Operators.LessEquals,
                TokenKind.Greater => Operators.Greater,
                TokenKind.GreaterEquals => Operators.GreaterEquals,
                TokenKind.Equals => Operators.EqualsOp,
                TokenKind.NotEquals => Operators.NotEquals,
                TokenKind.In => Operators.In,
                _ => null,
            };
            if (function == null)
                return left;
            var op = Advance();
            var right = ParseAddition();
            left = new CallExpr(NextId(op.Start), function, null, new[] { left, right });
        }
    }

    private Expr ParseAddition()
    {
        var left = ParseMultiplication();
        while (true)
        {
            string? function = Current.Kind switch
            {
                TokenKind.Plus => Operators.Add,
                TokenKind.Minus => Operators.Subtract,
                _ => null,
            };
            if (function == null)
                return left;
            var op = Advance();
            var right = ParseMultiplication();
            left = new CallExpr(NextId(op.Start), function, null, new[] { left, right });
        }
    }

    private Expr ParseMultiplication()
    {
        var left = ParseUnary();
        while (true)
        {
            string? function = Current.Kind switch
            {
                TokenKind.Star => Operators.Multiply,
                TokenKind.Slash => Operators.Divide,
                TokenKind.Percent => Operators.Modulo,
                _ => null,
            };
            if (function == null)
                return left;
            var op = Advance();
            var right = ParseUnary();
            left = new CallExpr(NextId(op.Start), function, null, new[] { left, right });
        }
    }

    private static bool IsNumeric(TokenKind kind) =>
        kind is TokenKind.Int or TokenKind.Uint or TokenKind.Double;

    // Unary = Member | "!" {"!"} Member | "-" {"-"} Member
    private Expr ParseUnary()
    {
        if (Current.Kind == TokenKind.Bang)
        {
            int count = 0;
            int first = Current.Start;
            while (Current.Kind == TokenKind.Bang)
            {
                Advance();
                count++;
            }
            var operand = ParseMember();
            if (count % 2 == 0)
                return operand;
            return new CallExpr(NextId(first), Operators.LogicalNot, null, new[] { operand });
        }

        if (Current.Kind == TokenKind.Minus)
        {
            // A single minus directly before a numeric literal is part of the literal, so that
            // the most negative int64 can be written and `-1` is a constant. Otherwise the
            // operator applies with the parity of the run of minus signs.
            if (!IsNumeric(PeekToken(1).Kind))
            {
                int count = 0;
                int first = Current.Start;
                while (Current.Kind == TokenKind.Minus)
                {
                    Advance();
                    count++;
                }
                var operand = ParseMember();
                if (count % 2 == 0)
                    return operand;
                return new CallExpr(NextId(first), Operators.Negate, null, new[] { operand });
            }
        }

        return ParseMember();
    }

    // Member = Primary | Member "." SELECTOR ["(" [ExprList] ")"] | Member "[" Expr "]"
    private Expr ParseMember()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                var dot = Advance();
                var name = ExpectSelector();
                if (Current.Kind == TokenKind.LParen)
                {
                    var paren = Advance();
                    var args = ParseExprList(TokenKind.RParen);
                    Expect(TokenKind.RParen, "')'");
                    expr = ReceiverCallOrMacro(paren.Start, name.Text, expr, args);
                }
                else
                {
                    expr = new SelectExpr(NextId(dot.Start), expr, name.Text, testOnly: false);
                }
            }
            else if (Current.Kind == TokenKind.LBracket)
            {
                var bracket = Advance();
                var index = ParseExpr();
                Expect(TokenKind.RBracket, "']'");
                expr = new CallExpr(NextId(bracket.Start), Operators.Index, null, new[] { expr, index });
            }
            else
            {
                return expr;
            }
        }
    }

    private Token ExpectSelector()
    {
        var tok = Current;
        if (tok.Kind == TokenKind.Ident)
            return Advance();
        if (tok.Kind == TokenKind.Reserved)
            throw new SyntaxException($"reserved identifier: {tok.Text}", tok.Start);
        throw new SyntaxException($"{Unexpected(tok)}, expecting IDENTIFIER", tok.Start);
    }

    private Expr ParsePrimary()
    {
        var tok = Current;
        switch (tok.Kind)
        {
            case TokenKind.Dot:
            case TokenKind.Ident:
            case TokenKind.Reserved:
                return ParseIdentOrCallOrMessage();

            case TokenKind.LParen:
            {
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.RParen, "')'");
                return inner;
            }

            case TokenKind.LBracket:
            {
                Advance();
                var elements = ParseExprList(TokenKind.RBracket);
                Expect(TokenKind.RBracket, "']'");
                return new ListExpr(NextId(tok.Start), elements);
            }

            case TokenKind.LBrace:
                return ParseMapLiteral();

            case TokenKind.Minus:
            {
                Advance();
                var number = Current;
                if (!IsNumeric(number.Kind))
                    throw new SyntaxException(Unexpected(number), number.Start);
                Advance();
                return NumericLiteral(number, negative: true, tok.Start);
            }

            case TokenKind.Int:
            case TokenKind.Uint:
            case TokenKind.Double:
                Advance();
                return NumericLiteral(tok, negative: false, tok.Start);

            case TokenKind.String:
                Advance();
                return LiteralExpr.String(NextId(tok.Start), (string)tok.Value!);

            case TokenKind.Bytes:
                Advance();
                return LiteralExpr.Bytes(NextId(tok.Start), (byte[])tok.Value!);

            case TokenKind.True:
                Advance();
                return LiteralExpr.Bool(NextId(tok.Start), true);

            case TokenKind.False:
                Advance();
                return LiteralExpr.Bool(NextId(tok.Start), false);

            case TokenKind.Null:
                Advance();
                return LiteralExpr.Null(NextId(tok.Start));

            default:
                throw new SyntaxException(Unexpected(tok), tok.Start);
        }
    }

    private Expr NumericLiteral(Token tok, bool negative, int offset)
    {
        var text = tok.Text;
        switch (tok.Kind)
        {
            case TokenKind.Int:
            {
                if (!TryParseInt(text, negative, out var value))
                    throw new SyntaxException("invalid int literal", offset);
                return LiteralExpr.Int(NextId(offset), value);
            }
            case TokenKind.Uint:
            {
                if (negative || !TryParseUint(text.Substring(0, text.Length - 1), out var value))
                    throw new SyntaxException("invalid uint literal", offset);
                return LiteralExpr.Uint(NextId(offset), value);
            }
            default:
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || double.IsInfinity(value) || double.IsNaN(value))
                    throw new SyntaxException("invalid double literal", offset);
                return LiteralExpr.Double(NextId(offset), negative ? -value : value);
            }
        }
    }

    private static bool TryParseInt(string text, bool negative, out long value)
    {
        value = 0;
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hex ? text.Substring(2) : text;
        // Accumulate as the magnitude in unsigned space, then apply the sign with the exact bounds
        // so that -9223372036854775808 is accepted while 9223372036854775808 is not.
        ulong magnitude = 0;
        foreach (var ch in digits)
        {
            int d = hex ? HexDigit(ch) : ch - '0';
            ulong limit = hex ? (ulong.MaxValue - (ulong)d) / 16 : (ulong.MaxValue - (ulong)d) / 10;
            if (magnitude > limit) return false;
            magnitude = magnitude * (ulong)(hex ? 16 : 10) + (ulong)d;
        }
        if (negative)
        {
            if (magnitude > (ulong)long.MaxValue + 1) return false;
            value = magnitude == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
            return true;
        }
        if (magnitude > (ulong)long.MaxValue) return false;
        value = (long)magnitude;
        return true;
    }

    private static bool TryParseUint(string text, out ulong value)
    {
        value = 0;
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hex ? text.Substring(2) : text;
        foreach (var ch in digits)
        {
            int d = hex ? HexDigit(ch) : ch - '0';
            ulong limit = (ulong.MaxValue - (ulong)d) / (ulong)(hex ? 16 : 10);
            if (value > limit) return false;
            value = value * (ulong)(hex ? 16 : 10) + (ulong)d;
        }
        return true;
    }

    private static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return c - 'A' + 10;
    }

    // ["."] IDENT ["(" [ExprList] ")"]  |  ["."] SELECTOR {"." SELECTOR} "{" [FieldInits] [","] "}"
    private Expr ParseIdentOrCallOrMessage()
    {
        int start = Current.Start;
        bool leadingDot = Accept(TokenKind.Dot);
        var first = Current;
        if (first.Kind == TokenKind.Reserved)
            throw new SyntaxException($"reserved identifier: {first.Text}", first.Start);
        if (first.Kind != TokenKind.Ident)
            throw new SyntaxException($"{Unexpected(first)}, expecting IDENTIFIER", first.Start);

        // Look ahead for a message literal: IDENT {"." IDENT} "{"
        int ahead = 1;
        while (PeekToken(ahead).Kind == TokenKind.Dot && PeekToken(ahead + 1).Kind is TokenKind.Ident or TokenKind.Reserved)
            ahead += 2;
        if (PeekToken(ahead).Kind == TokenKind.LBrace)
            return ParseMessageLiteral(start, leadingDot);

        Advance();
        var name = leadingDot ? "." + first.Text : first.Text;
        if (Current.Kind == TokenKind.LParen)
        {
            var paren = Advance();
            var args = ParseExprList(TokenKind.RParen);
            Expect(TokenKind.RParen, "')'");
            return GlobalCallOrMacro(paren.Start, name, args);
        }
        return new IdentExpr(NextId(first.Start), name);
    }

    private Expr ParseMessageLiteral(int start, bool leadingDot)
    {
        var names = new List<string>();
        while (true)
        {
            var tok = Current;
            if (tok.Kind == TokenKind.Reserved)
                throw new SyntaxException($"reserved identifier: {tok.Text}", tok.Start);
            Expect(TokenKind.Ident, "IDENTIFIER");
            names.Add(tok.Text);
            if (!Accept(TokenKind.Dot))
                break;
        }
        var brace = Expect(TokenKind.LBrace, "'{'");
        var messageName = (leadingDot ? "." : "") + string.Join(".", names);
        var entries = new List<FieldEntry>();
        while (Current.Kind != TokenKind.RBrace)
        {
            var fieldTok = Current;
            if (fieldTok.Kind == TokenKind.Reserved)
                throw new SyntaxException($"reserved identifier: {fieldTok.Text}", fieldTok.Start);
            Expect(TokenKind.Ident, "IDENTIFIER");
            var colon = Expect(TokenKind.Colon, "':'");
            var value = ParseExpr();
            entries.Add(new FieldEntry(NextId(colon.Start), fieldTok.Text, value));
            if (!Accept(TokenKind.Comma))
                break;
        }
        Expect(TokenKind.RBrace, "'}'");
        return new StructExpr(NextId(brace.Start), messageName, entries);
    }

    private Expr ParseMapLiteral()
    {
        var brace = Expect(TokenKind.LBrace, "'{'");
        var entries = new List<MapEntry>();
        while (Current.Kind != TokenKind.RBrace)
        {
            var key = ParseExpr();
            var colon = Expect(TokenKind.Colon, "':'");
            var value = ParseExpr();
            entries.Add(new MapEntry(NextId(colon.Start), key, value));
            if (!Accept(TokenKind.Comma))
                break;
        }
        Expect(TokenKind.RBrace, "'}'");
        return new MapExpr(NextId(brace.Start), entries);
    }

    private List<Expr> ParseExprList(TokenKind closer)
    {
        var list = new List<Expr>();
        if (Current.Kind == closer)
            return list;
        while (true)
        {
            list.Add(ParseExpr());
            if (!Accept(TokenKind.Comma))
                return list;
            // A trailing comma is permitted in list and map literals, not in argument lists.
            if (Current.Kind == closer && closer == TokenKind.RBracket)
                return list;
        }
    }

    private Expr GlobalCallOrMacro(int offset, string function, List<Expr> args)
    {
        if (TryExpandMacro(offset, function, null, args, out var expanded))
            return expanded;
        return new CallExpr(NextId(offset), function, null, args);
    }

    private Expr ReceiverCallOrMacro(int offset, string function, Expr target, List<Expr> args)
    {
        if (TryExpandMacro(offset, function, target, args, out var expanded))
            return expanded;
        return new CallExpr(NextId(offset), function, target, args);
    }

    private bool TryExpandMacro(int offset, string function, Expr? target, List<Expr> args, out Expr expanded)
    {
        expanded = null!;
        if (_macros.Count == 0 || !_macros.TryGetValue(Macro.MakeKey(function, args.Count, target != null), out var macro))
            return false;

        var helper = new MacroExprHelper(this, offset);
        expanded = macro.Expander(helper, target, args);
        if (_options.PopulateMacroCalls)
        {
            // Record the call as written, for diagnostics that want to show the macro.
            var call = new CallExpr(expanded.Id, function, target, args);
            _sourceInfo.AddMacroCall(expanded.Id, call);
        }
        return true;
    }
}
