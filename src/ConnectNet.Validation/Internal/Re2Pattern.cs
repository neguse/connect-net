using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectNet.Validation.Internal;

/// <summary>
/// Compiles a buf.validate <c>string.pattern</c> / <c>bytes.pattern</c> — which the spec defines
/// as RE2, evaluated through CEL's <c>matches()</c> — into a .NET regex that means the same
/// thing. The two engines do not agree when a pattern is handed over verbatim: .NET's
/// <c>$</c> also matches immediately before a trailing newline, its <c>\d</c>, <c>\w</c>,
/// <c>\s</c> and <c>\b</c> are Unicode-wide rather than the ASCII sets RE2 defines, POSIX
/// classes such as <c>[[:alnum:]]</c> are not understood at all, <c>\1</c> is a backreference
/// rather than an octal escape, and <c>-[...]</c> inside a character class is class
/// subtraction. Each of those would make the compiled pattern accept or reject values
/// differently from protovalidate-go for the same .proto.
///
/// The pattern is therefore parsed with RE2's grammar rather than rewritten textually:
/// anchors follow the <c>m</c> flag in scope at their position, character classes are rebuilt
/// item by item, and RE2-only spellings (<c>\x{...}</c>, <c>\Q...\E</c>,
/// <c>(?P&lt;name&gt;...)</c>, octal escapes) become their .NET equivalents. Constructs RE2
/// rejects (lookaround, backreferences, <c>(?#...)</c>, <c>\Z</c>, ...) and constructs whose
/// RE2 meaning cannot be expressed in .NET are rejected with <see cref="ArgumentException"/> —
/// the caller records that as a pattern violation — rather than compiled into something that
/// means something else.
/// </summary>
internal static class Re2Pattern
{
    /// <summary>
    /// Hard cap on regex execution time. A ReDoS-prone pattern (from the proto author) combined
    /// with a pathological value (from the caller) cannot stall a worker thread for longer.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // RE2's Perl character classes, as ASCII character-class members.
    private const string Digit = "0-9";
    private const string Word = "0-9A-Za-z_";
    private const string Space = @"\t\n\f\r ";

    // RE2's \b is the ASCII word boundary: exactly one side is an ASCII word character, with
    // the ends of the input counting as non-word.
    private const string WordBoundary =
        "(?:(?<![0-9A-Za-z_])(?=[0-9A-Za-z_])|(?<=[0-9A-Za-z_])(?![0-9A-Za-z_]))";
    private const string NonWordBoundary =
        "(?:(?<![0-9A-Za-z_])(?![0-9A-Za-z_])|(?<=[0-9A-Za-z_])(?=[0-9A-Za-z_]))";

    private static readonly Dictionary<string, string> PosixClasses = new()
    {
        ["alnum"] = "0-9A-Za-z",
        ["alpha"] = "A-Za-z",
        ["ascii"] = @"\x00-\x7F",
        ["blank"] = @"\t ",
        ["cntrl"] = @"\x00-\x1F\x7F",
        ["digit"] = "0-9",
        ["graph"] = "!-~",
        ["lower"] = "a-z",
        ["print"] = " -~",
        ["punct"] = @"!-/:-@\[-`{-~",
        ["space"] = @"\t-\r ",
        ["upper"] = "A-Z",
        ["word"] = "0-9A-Za-z_",
        ["xdigit"] = "0-9A-Fa-f",
    };

    // Caches compilation outcomes, failures included, so an invalid pattern is discovered once
    // instead of re-translated for every value it is evaluated against. Patterns come from
    // .proto definitions, but descriptors can be loaded at runtime (per-tenant schemas), so
    // the cache is bounded: past the cap, patterns compile per call rather than growing
    // process-lifetime state.
    private const int MaxCacheEntries = 1024;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private sealed class CacheEntry
    {
        public Regex? Regex;
        public string? Error;
    }

    /// <summary>
    /// Returns the compiled .NET equivalent of an RE2 pattern.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The pattern is not valid, or uses a construct whose RE2 meaning cannot be preserved.
    /// </exception>
    public static Regex GetRegex(string pattern)
    {
        if (!Cache.TryGetValue(pattern, out var entry))
        {
            entry = Compile(pattern);
            if (Cache.Count < MaxCacheEntries)
                entry = Cache.GetOrAdd(pattern, entry);
        }
        if (entry.Error != null)
            throw new ArgumentException(entry.Error);
        return entry.Regex!;
    }

    private static CacheEntry Compile(string pattern)
    {
        try
        {
            return new CacheEntry
            {
                Regex = new Regex(Translate(pattern), RegexOptions.CultureInvariant, MatchTimeout),
            };
        }
        catch (ArgumentException ex) // RegexParseException included
        {
            return new CacheEntry { Error = ex.Message };
        }
    }

    internal static string Translate(string pattern) => new Translator(pattern).Run();

    private sealed class Translator
    {
        private readonly string _p;
        private readonly StringBuilder _sb;
        private int _i;

        // Whether RE2's `m` flag is on at the current position; it decides how `^` and `$`
        // are rewritten. An inline flag group applies to the rest of its enclosing group, so
        // the state is saved on '(' and restored on ')'.
        private bool _multiline;
        private readonly Stack<bool> _groupFlags = new();

        public Translator(string pattern)
        {
            _p = pattern;
            _sb = new StringBuilder(pattern.Length + 16);
        }

        public string Run()
        {
            while (_i < _p.Length)
            {
                var c = _p[_i];
                switch (c)
                {
                    case '\\':
                        AppendEscape();
                        break;
                    case '[':
                        AppendClass();
                        break;
                    case '(':
                        AppendGroupStart();
                        break;
                    case ')':
                        if (_groupFlags.Count > 0)
                            _multiline = _groupFlags.Pop();
                        _sb.Append(')');
                        _i++;
                        break;
                    case '^':
                        _sb.Append(_multiline ? "^" : @"\A");
                        _i++;
                        break;
                    case '$':
                        _sb.Append(_multiline ? "$" : @"\z");
                        _i++;
                        break;
                    default:
                        _sb.Append(c);
                        _i++;
                        break;
                }
            }
            return _sb.ToString();
        }

        // --- groups ---

        private void AppendGroupStart()
        {
            if (_i + 1 >= _p.Length || _p[_i + 1] != '?')
            {
                _groupFlags.Push(_multiline);
                _sb.Append('(');
                _i++;
                return;
            }
            if (_i + 2 >= _p.Length)
                throw new ArgumentException("unterminated group");

            switch (_p[_i + 2])
            {
                case ':':
                    _groupFlags.Push(_multiline);
                    _sb.Append("(?:");
                    _i += 3;
                    return;
                case 'P':
                case '<':
                    AppendNamedGroup();
                    return;
                case '=':
                case '!':
                    throw new ArgumentException("lookahead is not supported by RE2");
                case '#':
                    throw new ArgumentException("(?#...) comments are not supported by RE2");
                case '\'':
                    throw new ArgumentException("(?'name'...) groups are not supported by RE2");
                default:
                    AppendFlagGroup();
                    return;
            }
        }

        private void AppendNamedGroup()
        {
            // RE2 spells named groups (?P<name>...) and (?<name>...); .NET understands only
            // the latter. (?<= and (?<! are .NET lookbehind, which RE2 rejects.
            var lt = _p[_i + 2] == 'P' ? _i + 3 : _i + 2;
            if (lt >= _p.Length || _p[lt] != '<')
                throw new ArgumentException("malformed named group");
            if (lt + 1 < _p.Length && (_p[lt + 1] == '=' || _p[lt + 1] == '!'))
                throw new ArgumentException("lookbehind is not supported by RE2");
            var gt = _p.IndexOf('>', lt + 1);
            if (gt < 0 || gt == lt + 1)
                throw new ArgumentException("malformed group name");
            _groupFlags.Push(_multiline);
            _sb.Append("(?<").Append(_p, lt + 1, gt - (lt + 1)).Append('>');
            _i = gt + 1;
        }

        private void AppendFlagGroup()
        {
            // (?flags) or (?flags:...) with flags drawn from RE2's i, m, s, U and '-' to
            // clear; anything else after "(?" is a construct RE2 does not have. .NET reads
            // i, m, s and '-' the same way, so the group text is copied verbatim.
            var j = _i + 2;
            var negating = false;
            var lastWasDash = false;
            var sawFlag = false;
            bool? m = null;
            for (; j < _p.Length && _p[j] != ')' && _p[j] != ':'; j++)
            {
                lastWasDash = false;
                switch (_p[j])
                {
                    case '-':
                        if (negating)
                            throw new ArgumentException("malformed flag group");
                        negating = true;
                        lastWasDash = true;
                        break;
                    case 'i':
                    case 's':
                        sawFlag = true;
                        break;
                    case 'm':
                        sawFlag = true;
                        m = !negating;
                        break;
                    case 'U':
                        // RE2's U swaps quantifier greediness; .NET has no equivalent flag.
                        throw new ArgumentException("the RE2 'U' flag cannot be given .NET semantics");
                    default:
                        throw new ArgumentException($"(?{_p[j]} is not a group or flag RE2 supports");
                }
            }
            if (j >= _p.Length)
                throw new ArgumentException("unterminated flag group");
            if (!sawFlag || lastWasDash)
                throw new ArgumentException("malformed flag group");

            if (_p[j] == ':')
            {
                // (?flags:...): the flags are scoped to the group.
                _groupFlags.Push(_multiline);
            }
            if (m.HasValue)
                _multiline = m.Value;
            _sb.Append(_p, _i, j - _i + 1);
            _i = j + 1;
        }

        // --- escapes outside a character class ---

        private void AppendEscape()
        {
            if (_i + 1 >= _p.Length)
                throw new ArgumentException("pattern ends with a trailing backslash");
            var e = _p[_i + 1];
            switch (e)
            {
                case 'd': _sb.Append('[').Append(Digit).Append(']'); _i += 2; return;
                case 'w': _sb.Append('[').Append(Word).Append(']'); _i += 2; return;
                case 's': _sb.Append('[').Append(Space).Append(']'); _i += 2; return;
                case 'D': _sb.Append("[^").Append(Digit).Append(']'); _i += 2; return;
                case 'W': _sb.Append("[^").Append(Word).Append(']'); _i += 2; return;
                case 'S': _sb.Append("[^").Append(Space).Append(']'); _i += 2; return;
                case 'b': _sb.Append(WordBoundary); _i += 2; return;
                case 'B': _sb.Append(NonWordBoundary); _i += 2; return;
                case 'A':
                case 'z':
                case 'a':
                case 'f':
                case 't':
                case 'n':
                case 'r':
                case 'v':
                    _sb.Append('\\').Append(e);
                    _i += 2;
                    return;
                case 'p':
                case 'P':
                    AppendUnicodeClass();
                    return;
                case 'x':
                    AppendCodePoint(ReadHex(), inClass: false);
                    return;
                case 'Q':
                    AppendQuoted();
                    return;
                case 'E':
                    _i += 2; // a stray \E closes nothing and matches nothing
                    return;
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                    // RE2 reads \123 as an octal character code, where .NET would read a
                    // backreference; emit the character it denotes.
                    AppendCodePoint(ReadOctal(), inClass: false);
                    return;
                case 'Z':
                    throw new ArgumentException(@"\Z is not supported by RE2 (use \z)");
                default:
                    if (!char.IsLetterOrDigit(e))
                    {
                        _sb.Append('\\').Append(e);
                        _i += 2;
                        return;
                    }
                    throw new ArgumentException($@"escape \{e} is not supported by RE2");
            }
        }

        private void AppendUnicodeClass()
        {
            // \pN or \p{Name}: RE2 and .NET share the spelling; names they disagree on fail
            // at Regex construction (fail closed).
            if (_i + 2 >= _p.Length)
                throw new ArgumentException(@"malformed \p escape");
            if (_p[_i + 2] == '{')
            {
                var close = _p.IndexOf('}', _i + 3);
                if (close < 0)
                    throw new ArgumentException(@"unterminated \p{...} escape");
                _sb.Append(_p, _i, close - _i + 1);
                _i = close + 1;
            }
            else
            {
                _sb.Append(_p, _i, 3);
                _i += 3;
            }
        }

        private void AppendQuoted()
        {
            // \Q...\E: everything through \E (or the end of the pattern) is literal text.
            var j = _i + 2;
            while (j < _p.Length)
            {
                if (_p[j] == '\\' && j + 1 < _p.Length && _p[j + 1] == 'E')
                {
                    j += 2;
                    _i = j;
                    return;
                }
                AppendLiteralChar(_p[j]);
                j++;
            }
            _i = j;
        }

        private void AppendLiteralChar(char c)
        {
            if (c is '\\' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}'
                or '^' or '$' or '.' or '|')
            {
                _sb.Append('\\');
            }
            _sb.Append(c);
        }

        private void AppendCodePoint(int value, bool inClass)
        {
            if (value <= 0xFFFF)
            {
                _sb.Append(@"\u").Append(value.ToString("X4"));
                return;
            }
            if (inClass)
                throw new ArgumentException("a code point above U+FFFF cannot be a .NET character-class member");
            // .NET matches UTF-16 code units, so a supplementary code point is its surrogate
            // pair, grouped so a following quantifier applies to the whole character.
            var s = char.ConvertFromUtf32(value);
            _sb.Append("(?:")
                .Append(@"\u").Append(((int)s[0]).ToString("X4"))
                .Append(@"\u").Append(((int)s[1]).ToString("X4"))
                .Append(')');
        }

        /// <summary>Reads the octal escape at the cursor and returns its character code.</summary>
        private int ReadOctal()
        {
            var value = 0;
            var j = _i + 1;
            for (var n = 0; j < _p.Length && n < 3 && _p[j] >= '0' && _p[j] <= '7'; j++, n++)
                value = value * 8 + (_p[j] - '0');
            _i = j;
            return value;
        }

        /// <summary>Reads <c>\xNN</c> or <c>\x{...}</c> at the cursor and returns the code point.</summary>
        private int ReadHex()
        {
            var j = _i + 2;
            if (j < _p.Length && _p[j] == '{')
            {
                var close = _p.IndexOf('}', j + 1);
                if (close < 0 || close == j + 1)
                    throw new ArgumentException(@"malformed \x{...} escape");
                var value = 0;
                for (var k = j + 1; k < close; k++)
                {
                    var d = HexDigit(_p[k]);
                    if (d < 0)
                        throw new ArgumentException(@"malformed \x{...} escape");
                    value = value * 16 + d;
                    if (value > 0x10FFFF)
                        throw new ArgumentException(@"\x{...} exceeds U+10FFFF");
                }
                _i = close + 1;
                return value;
            }
            if (j + 1 < _p.Length && HexDigit(_p[j]) >= 0 && HexDigit(_p[j + 1]) >= 0)
            {
                var value = HexDigit(_p[j]) * 16 + HexDigit(_p[j + 1]);
                _i = j + 2;
                return value;
            }
            throw new ArgumentException(@"malformed \x escape (RE2 requires \xNN or \x{...})");
        }

        private static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        // --- character classes ---

        private void AppendClass()
        {
            _sb.Append('[');
            _i++;
            if (_i < _p.Length && _p[_i] == '^')
            {
                _sb.Append('^');
                _i++;
            }

            // Items are read the way RE2 reads them: whole-set items (POSIX classes, \d and
            // friends, \p) stand alone, and a '-' forms a range only between two single
            // characters — otherwise it is a literal member, which .NET must see escaped so
            // it can neither form a range with a neighbouring set expansion nor start a
            // .NET-only "-[...]" class subtraction.
            var first = true; // ']' is an ordinary member when it is the first item
            while (true)
            {
                if (_i >= _p.Length)
                    throw new ArgumentException("unterminated character class");
                if (_p[_i] == ']' && !first)
                {
                    _sb.Append(']');
                    _i++;
                    return;
                }
                first = false;

                if (_p[_i] == '[' && TryAppendPosixClass())
                    continue;
                if (_p[_i] == '\\' && _i + 1 < _p.Length && IsClassSetEscape(_p[_i + 1]))
                {
                    AppendClassSetEscape();
                    continue;
                }

                var (loValue, loEmit) = ReadClassChar();
                if (_i + 1 < _p.Length && _p[_i] == '-' && _p[_i + 1] != ']')
                {
                    _i++; // the '-' of a range
                    var (hiValue, hiEmit) = ReadClassChar();
                    if (hiValue < loValue)
                        throw new ArgumentException("invalid character class range");
                    _sb.Append(loEmit).Append('-').Append(hiEmit);
                }
                else
                {
                    _sb.Append(loEmit);
                }
            }
        }

        private static bool IsClassSetEscape(char e)
            => e is 'd' or 's' or 'w' or 'D' or 'S' or 'W' or 'p' or 'P';

        private void AppendClassSetEscape()
        {
            switch (_p[_i + 1])
            {
                case 'd': _sb.Append(Digit); _i += 2; return;
                case 'w': _sb.Append(Word); _i += 2; return;
                case 's': _sb.Append(Space); _i += 2; return;
                case 'p':
                case 'P':
                    AppendUnicodeClass();
                    return;
                default:
                    // The union of a negated set with the rest of the class is not something
                    // a single .NET character class can express.
                    throw new ArgumentException(
                        $@"\{_p[_i + 1]} inside a character class cannot be given RE2 semantics in .NET");
            }
        }

        /// <summary>
        /// Reads one single-character class member (literal or escape) and returns its code
        /// point plus the text to emit for it. Used for range endpoints too, so set-valued
        /// escapes are rejected here the way RE2 rejects <c>[a-\d]</c>.
        /// </summary>
        private (int Value, string Emit) ReadClassChar()
        {
            var c = _p[_i];
            if (c != '\\')
            {
                _i++;
                return (c, EscapeClassMember(c));
            }
            if (_i + 1 >= _p.Length)
                throw new ArgumentException("pattern ends with a trailing backslash");
            var e = _p[_i + 1];
            switch (e)
            {
                case 'a': _i += 2; return ('\a', @"\a");
                case 'f': _i += 2; return ('\f', @"\f");
                case 't': _i += 2; return ('\t', @"\t");
                case 'n': _i += 2; return ('\n', @"\n");
                case 'r': _i += 2; return ('\r', @"\r");
                case 'v': _i += 2; return ('\v', @"\v");
                case 'x':
                {
                    var value = ReadHex();
                    if (value > 0xFFFF)
                        throw new ArgumentException("a code point above U+FFFF cannot be a .NET character-class member");
                    return (value, @"\u" + value.ToString("X4"));
                }
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                {
                    var value = ReadOctal();
                    return (value, @"\x" + value.ToString("X2"));
                }
                case 'b':
                case 'B':
                    throw new ArgumentException($@"\{e} inside a character class is not supported by RE2");
                case 'Q':
                case 'E':
                    throw new ArgumentException(@"\Q...\E inside a character class is not supported by this translation");
                default:
                    if (!char.IsLetterOrDigit(e))
                    {
                        _i += 2;
                        return (e, "\\" + e);
                    }
                    throw new ArgumentException($@"escape \{e} is not supported by RE2 inside a character class");
            }
        }

        private static string EscapeClassMember(char c) => c switch
        {
            '[' or ']' or '\\' or '-' or '^' => "\\" + c,
            _ => c.ToString(),
        };

        /// <summary>
        /// Expands a POSIX class such as <c>[:alnum:]</c> into its ASCII members, in place
        /// inside the surrounding character class. Returns false when what follows is not a
        /// POSIX class, in which case the '[' is an ordinary member.
        /// </summary>
        private bool TryAppendPosixClass()
        {
            if (_i + 1 >= _p.Length || _p[_i + 1] != ':')
                return false;

            var end = _p.IndexOf(":]", _i + 2, StringComparison.Ordinal);
            if (end < 0)
                return false;

            var name = _p.Substring(_i + 2, end - (_i + 2));
            if (name.Length > 0 && name[0] == '^')
            {
                // The complement of an ASCII set unioned with the rest of the class: same
                // problem as \D inside a class.
                throw new ArgumentException($"negated POSIX class [:{name}:] cannot be given RE2 semantics in .NET");
            }
            if (!PosixClasses.TryGetValue(name, out var members))
                throw new ArgumentException($"unknown POSIX class [:{name}:]");

            _sb.Append(members);
            _i = end + 2;
            return true;
        }
    }
}
