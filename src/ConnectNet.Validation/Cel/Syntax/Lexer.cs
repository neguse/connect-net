using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ConnectNet.Validation.Cel.Syntax;

internal enum TokenKind
{
    Eof,
    Error,
    Ident,
    Reserved,
    Int,
    Uint,
    Double,
    String,
    Bytes,
    True,
    False,
    Null,
    In,
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Dot,
    Comma,
    Colon,
    Question,
    Bang,
    Minus,
    Plus,
    Star,
    Slash,
    Percent,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    Equals,
    NotEquals,
    AndAnd,
    OrOr,
}

internal readonly struct Token
{
    public Token(TokenKind kind, int start, int end, string text, object? value = null)
    {
        Kind = kind;
        Start = start;
        End = end;
        Text = text;
        Value = value;
    }

    public TokenKind Kind { get; }

    /// <summary>Code point offset of the first character.</summary>
    public int Start { get; }

    /// <summary>Code point offset one past the last character.</summary>
    public int End { get; }

    /// <summary>The source text of identifiers and numbers; the error message for <see cref="TokenKind.Error"/>.</summary>
    public string Text { get; }

    /// <summary>Decoded value of string (<see cref="string"/>) and bytes (<c>byte[]</c>) literals.</summary>
    public object? Value { get; }

    public bool Is(TokenKind kind) => Kind == kind;

    public override string ToString() => Kind + (Text.Length > 0 ? "(" + Text + ")" : "");
}

/// <summary>
/// Tokenizer for the CEL lexis. Operates on code points so that positions, string contents and
/// error offsets count Unicode scalar values exactly as the specification does.
/// </summary>
internal sealed class Lexer
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "as", "break", "const", "continue", "else", "for", "function", "if", "import", "let",
        "loop", "package", "namespace", "return", "var", "void", "while",
    };

    private readonly int[] _cp;
    private int _pos;

    public Lexer(Source source)
    {
        Source = source;
        _cp = source.CodePoints;
    }

    public Source Source { get; }

    public static bool IsReservedWord(string name) => ReservedWords.Contains(name);

    /// <summary>Lexes the whole source. The last token is always <see cref="TokenKind.Eof"/>.</summary>
    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var tok = Next();
            tokens.Add(tok);
            if (tok.Kind == TokenKind.Eof || tok.Kind == TokenKind.Error)
                break;
        }
        return tokens;
    }

    private int Peek(int ahead = 0)
    {
        int i = _pos + ahead;
        return i < _cp.Length ? _cp[i] : -1;
    }

    private Token Next()
    {
        SkipTrivia();
        if (_pos >= _cp.Length)
            return new Token(TokenKind.Eof, _pos, _pos, "");

        int start = _pos;
        int c = _cp[_pos];

        if (IsIdentStart(c))
        {
            // String and bytes prefixes: r"...", b"...", br"...", in any letter case.
            if (TryLexPrefixedString(start, out var strTok))
                return strTok;
            while (IsIdentPart(Peek())) _pos++;
            var text = Source.Slice(start, _pos);
            var kind = text switch
            {
                "true" => TokenKind.True,
                "false" => TokenKind.False,
                "null" => TokenKind.Null,
                "in" => TokenKind.In,
                _ => ReservedWords.Contains(text) ? TokenKind.Reserved : TokenKind.Ident,
            };
            return new Token(kind, start, _pos, text);
        }

        if (IsDigit(c) || (c == '.' && IsDigit(Peek(1))))
            return LexNumber(start);

        if (c == '"' || c == '\'')
            return LexString(start, isRaw: false, isBytes: false);

        _pos++;
        switch (c)
        {
            case '(': return Punct(TokenKind.LParen, start);
            case ')': return Punct(TokenKind.RParen, start);
            case '[': return Punct(TokenKind.LBracket, start);
            case ']': return Punct(TokenKind.RBracket, start);
            case '{': return Punct(TokenKind.LBrace, start);
            case '}': return Punct(TokenKind.RBrace, start);
            case '.': return Punct(TokenKind.Dot, start);
            case ',': return Punct(TokenKind.Comma, start);
            case ':': return Punct(TokenKind.Colon, start);
            case '?': return Punct(TokenKind.Question, start);
            case '+': return Punct(TokenKind.Plus, start);
            case '-': return Punct(TokenKind.Minus, start);
            case '*': return Punct(TokenKind.Star, start);
            case '/': return Punct(TokenKind.Slash, start);
            case '%': return Punct(TokenKind.Percent, start);
            case '!':
                if (Peek() == '=') { _pos++; return Punct(TokenKind.NotEquals, start); }
                return Punct(TokenKind.Bang, start);
            case '<':
                if (Peek() == '=') { _pos++; return Punct(TokenKind.LessEquals, start); }
                return Punct(TokenKind.Less, start);
            case '>':
                if (Peek() == '=') { _pos++; return Punct(TokenKind.GreaterEquals, start); }
                return Punct(TokenKind.Greater, start);
            case '=':
                if (Peek() == '=') { _pos++; return Punct(TokenKind.Equals, start); }
                return Error(start, "token recognition error at: '='");
            case '&':
                if (Peek() == '&') { _pos++; return Punct(TokenKind.AndAnd, start); }
                return Error(start, "token recognition error at: '&'");
            case '|':
                if (Peek() == '|') { _pos++; return Punct(TokenKind.OrOr, start); }
                return Error(start, "token recognition error at: '|'");
            default:
                return Error(start, "token recognition error at: '" + Describe(c) + "'");
        }
    }

    private Token Punct(TokenKind kind, int start) => new(kind, start, _pos, "");

    private Token Error(int start, string message)
    {
        return new Token(TokenKind.Error, start, _pos, message);
    }

    private static string Describe(int c)
    {
        if (c < 0 || (c >= 0xD800 && c <= 0xDFFF))
            return "\\u" + (c & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
        if (c < 0x20 || c == 0x7f)
            return "\\u" + c.ToString("x4", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        Source.AppendCodePoint(sb, c);
        return sb.ToString();
    }

    private void SkipTrivia()
    {
        while (_pos < _cp.Length)
        {
            int c = _cp[_pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
            {
                _pos++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _cp.Length && _cp[_pos] != '\n') _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private static bool IsIdentStart(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private static bool IsIdentPart(int c) => IsIdentStart(c) || IsDigit(c);
    private static bool IsDigit(int c) => c >= '0' && c <= '9';
    private static bool IsHexDigit(int c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private bool TryLexPrefixedString(int start, out Token token)
    {
        token = default;
        int c0 = Peek();
        int c1 = Peek(1);
        int c2 = Peek(2);
        bool isBytes = false, isRaw = false;
        int prefixLen;
        if ((c0 == 'b' || c0 == 'B') && (c1 == 'r' || c1 == 'R') && (c2 == '"' || c2 == '\''))
        {
            isBytes = true; isRaw = true; prefixLen = 2;
        }
        else if ((c0 == 'b' || c0 == 'B') && (c1 == '"' || c1 == '\''))
        {
            isBytes = true; prefixLen = 1;
        }
        else if ((c0 == 'r' || c0 == 'R') && (c1 == '"' || c1 == '\''))
        {
            isRaw = true; prefixLen = 1;
        }
        else
        {
            return false;
        }
        _pos += prefixLen;
        token = LexString(start, isRaw, isBytes);
        return true;
    }

    private Token LexNumber(int start)
    {
        bool isDouble = false;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X') && IsHexDigit(Peek(2)))
        {
            _pos += 2;
            while (IsHexDigit(Peek())) _pos++;
            return FinishInteger(start);
        }

        while (IsDigit(Peek())) _pos++;
        if (Peek() == '.' && IsDigit(Peek(1)))
        {
            isDouble = true;
            _pos++;
            while (IsDigit(Peek())) _pos++;
        }
        if (Peek() == 'e' || Peek() == 'E')
        {
            int save = _pos;
            _pos++;
            if (Peek() == '+' || Peek() == '-') _pos++;
            if (IsDigit(Peek()))
            {
                isDouble = true;
                while (IsDigit(Peek())) _pos++;
            }
            else
            {
                _pos = save;
            }
        }
        if (isDouble)
            return new Token(TokenKind.Double, start, _pos, Source.Slice(start, _pos));
        return FinishInteger(start);
    }

    private Token FinishInteger(int start)
    {
        if (Peek() == 'u' || Peek() == 'U')
        {
            _pos++;
            return new Token(TokenKind.Uint, start, _pos, Source.Slice(start, _pos));
        }
        return new Token(TokenKind.Int, start, _pos, Source.Slice(start, _pos));
    }

    private Token LexString(int start, bool isRaw, bool isBytes)
    {
        int quote = _cp[_pos];
        bool triple = Peek(1) == quote && Peek(2) == quote;
        _pos += triple ? 3 : 1;

        var text = new StringBuilder();
        var bytes = isBytes ? new List<byte>() : null;

        while (true)
        {
            int c = Peek();
            if (c < 0)
                return Error(start, "unterminated string literal");

            if (c == quote)
            {
                if (!triple)
                {
                    _pos++;
                    break;
                }
                if (Peek(1) == quote && Peek(2) == quote)
                {
                    _pos += 3;
                    break;
                }
                _pos++;
                Emit(text, bytes, quote, isBytes);
                continue;
            }

            if (!triple && (c == '\n' || c == '\r'))
                return Error(start, "unterminated string literal");

            if (c >= 0xD800 && c <= 0xDFFF)
                return Error(_pos, "invalid UTF-8 in string literal");

            if (c == '\\' && !isRaw)
            {
                int escStart = _pos;
                _pos++;
                if (!LexEscape(text, bytes, isBytes, out var error))
                    return Error(escStart, error!);
                continue;
            }

            _pos++;
            if (c == '\r')
            {
                // CEL normalizes CR and CRLF inside multi-line literals to LF.
                if (Peek() == '\n') _pos++;
                c = '\n';
            }
            Emit(text, bytes, c, isBytes);
        }

        if (isBytes)
            return new Token(TokenKind.Bytes, start, _pos, "", bytes!.ToArray());
        return new Token(TokenKind.String, start, _pos, "", text.ToString());
    }

    private static void Emit(StringBuilder text, List<byte>? bytes, int cp, bool isBytes)
    {
        if (isBytes)
        {
            if (cp < 0x80)
            {
                bytes!.Add((byte)cp);
            }
            else
            {
                var tmp = new StringBuilder(2);
                Source.AppendCodePoint(tmp, cp);
                bytes!.AddRange(Encoding.UTF8.GetBytes(tmp.ToString()));
            }
        }
        else
        {
            Source.AppendCodePoint(text, cp);
        }
    }

    private bool LexEscape(StringBuilder text, List<byte>? bytes, bool isBytes, out string? error)
    {
        error = null;
        int c = Peek();
        if (c < 0)
        {
            error = "unable to unescape string, found '\\' as last character";
            return false;
        }
        _pos++;
        switch (c)
        {
            case 'a': Emit(text, bytes, '\a', isBytes); return true;
            case 'b': Emit(text, bytes, '\b', isBytes); return true;
            case 'f': Emit(text, bytes, '\f', isBytes); return true;
            case 'n': Emit(text, bytes, '\n', isBytes); return true;
            case 'r': Emit(text, bytes, '\r', isBytes); return true;
            case 't': Emit(text, bytes, '\t', isBytes); return true;
            case 'v': Emit(text, bytes, '\v', isBytes); return true;
            case '\\': Emit(text, bytes, '\\', isBytes); return true;
            case '\'': Emit(text, bytes, '\'', isBytes); return true;
            case '"': Emit(text, bytes, '"', isBytes); return true;
            case '`': Emit(text, bytes, '`', isBytes); return true;
            case '?': Emit(text, bytes, '?', isBytes); return true;

            case 'x':
            case 'X':
            case 'u':
            case 'U':
            {
                int digits = c is 'x' or 'X' ? 2 : c == 'u' ? 4 : 8;
                if (isBytes && c is 'u' or 'U')
                {
                    error = "unable to unescape string";
                    return false;
                }
                int value = 0;
                for (int i = 0; i < digits; i++)
                {
                    int h = Peek();
                    if (!IsHexDigit(h))
                    {
                        error = "unable to unescape string";
                        return false;
                    }
                    _pos++;
                    value = (value << 4) | HexValue(h);
                }
                if (isBytes)
                {
                    // \x in a bytes literal denotes one octet.
                    bytes!.Add((byte)value);
                    return true;
                }
                if (!IsValidCodePoint(value))
                {
                    error = "invalid unicode code point";
                    return false;
                }
                Emit(text, bytes, value, isBytes);
                return true;
            }

            case '0':
            case '1':
            case '2':
            case '3':
            {
                int value = c - '0';
                for (int i = 0; i < 2; i++)
                {
                    int d = Peek();
                    if (d < '0' || d > '7')
                    {
                        error = "unable to unescape octal sequence in string";
                        return false;
                    }
                    _pos++;
                    value = value * 8 + (d - '0');
                }
                if (isBytes)
                {
                    bytes!.Add((byte)value);
                    return true;
                }
                Emit(text, bytes, value, isBytes);
                return true;
            }

            default:
                error = "unable to unescape string";
                return false;
        }
    }

    private static int HexValue(int c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return c - 'A' + 10;
    }

    private static bool IsValidCodePoint(int value) =>
        value >= 0 && value <= 0x10FFFF && !(value >= 0xD800 && value <= 0xDFFF);
}
