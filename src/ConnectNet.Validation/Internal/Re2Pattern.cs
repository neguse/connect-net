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
/// <c>\s</c> and <c>\b</c> are Unicode-wide rather than the ASCII sets RE2 defines, and POSIX
/// classes such as <c>[[:alnum:]]</c> are not understood at all (.NET reads the brackets as
/// ordinary members). Each of those makes the compiled pattern accept values the schema author
/// declared invalid and protovalidate-go rejects.
///
/// Constructs whose RE2 meaning cannot be expressed as a single .NET construct are rejected with
/// <see cref="ArgumentException"/> — the caller records that as a pattern violation — rather than
/// compiled into something that means something else.
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

    // Patterns come from .proto definitions, never from request data, so this set is bounded by
    // the schema and caching it costs nothing an attacker can grow.
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    /// <summary>
    /// Returns the compiled .NET equivalent of an RE2 pattern.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The pattern is not valid, or uses a construct whose RE2 meaning cannot be preserved.
    /// </exception>
    public static Regex GetRegex(string pattern)
        => Cache.GetOrAdd(pattern, p => new Regex(Translate(p), RegexOptions.CultureInvariant, MatchTimeout));

    internal static string Translate(string pattern)
    {
        // Under RE2's `m` flag `^` and `$` really are line anchors, exactly as in .NET, so the
        // anchors are left alone. A pattern that turns the flag back off (`(?i-m)`) is treated
        // the same way: keeping .NET's reading is the conservative choice.
        var lineAnchors = HasMultilineFlag(pattern);
        var sb = new StringBuilder(pattern.Length + 16);
        var inClass = false;

        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    sb.Append(c); // trailing backslash: let Regex report the malformed pattern
                    break;
                }
                AppendEscape(pattern[++i], inClass, sb);
                continue;
            }

            if (inClass)
            {
                if (c == '[' && TryAppendPosixClass(pattern, ref i, sb))
                    continue;
                if (c == ']')
                    inClass = false;
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    inClass = true;
                    sb.Append('[');
                    // A '^' straight after '[' negates the class, and a ']' straight after that
                    // is a literal member: copy both through before ordinary class scanning.
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                    {
                        sb.Append('^');
                        i++;
                    }
                    if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                    {
                        sb.Append(@"\]");
                        i++;
                    }
                    break;
                case '^':
                    sb.Append(lineAnchors ? "^" : @"\A");
                    break;
                case '$':
                    sb.Append(lineAnchors ? "$" : @"\z");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendEscape(char escaped, bool inClass, StringBuilder sb)
    {
        switch (escaped)
        {
            case 'd':
                sb.Append(inClass ? Digit : "[" + Digit + "]");
                break;
            case 'w':
                sb.Append(inClass ? Word : "[" + Word + "]");
                break;
            case 's':
                sb.Append(inClass ? Space : "[" + Space + "]");
                break;
            case 'D':
            case 'W':
            case 'S':
                // Inside a class this is a union with a negated set, which a single .NET
                // character class cannot express.
                if (inClass)
                {
                    throw new ArgumentException(
                        $@"\{escaped} inside a character class cannot be given RE2 semantics in .NET");
                }
                sb.Append(escaped == 'D' ? "[^" + Digit + "]"
                    : escaped == 'W' ? "[^" + Word + "]"
                    : "[^" + Space + "]");
                break;
            case 'b' when !inClass:
                sb.Append(WordBoundary);
                break;
            case 'B' when !inClass:
                sb.Append(NonWordBoundary);
                break;
            default:
                sb.Append('\\').Append(escaped);
                break;
        }
    }

    /// <summary>
    /// Expands a POSIX class such as <c>[:alnum:]</c> into its ASCII members, in place inside the
    /// surrounding character class. Returns false when what follows is not a POSIX class, in
    /// which case the '[' is an ordinary member and the caller copies it through.
    /// </summary>
    private static bool TryAppendPosixClass(string pattern, ref int i, StringBuilder sb)
    {
        if (i + 1 >= pattern.Length || pattern[i + 1] != ':')
            return false;

        var end = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
        if (end < 0)
            return false;

        var name = pattern.Substring(i + 2, end - (i + 2));
        if (name.Length > 0 && name[0] == '^')
        {
            // The complement of an ASCII set unioned with the rest of the class: same problem
            // as \D inside a class.
            throw new ArgumentException($"negated POSIX class [:{name}:] cannot be given RE2 semantics in .NET");
        }
        if (!PosixClasses.TryGetValue(name, out var members))
            throw new ArgumentException($"unknown POSIX class [:{name}:]");

        sb.Append(members);
        i = end + 1; // consume through the ']' that closes ":]"
        return true;
    }

    /// <summary>
    /// Whether the pattern turns on RE2's <c>m</c> flag anywhere, via <c>(?m)</c> or
    /// <c>(?m:...)</c>. Only the flag letters RE2 and .NET share are scanned, so a group like
    /// <c>(?&lt;name&gt;...)</c> is not mistaken for a flag group.
    /// </summary>
    private static bool HasMultilineFlag(string pattern)
    {
        for (int i = 0; i + 2 < pattern.Length; i++)
        {
            if (pattern[i] != '(' || pattern[i + 1] != '?')
                continue;

            var sawM = false;
            var j = i + 2;
            for (; j < pattern.Length && IsFlagChar(pattern[j]); j++)
            {
                if (pattern[j] == 'm')
                    sawM = true;
            }
            if (sawM && j < pattern.Length && (pattern[j] == ')' || pattern[j] == ':'))
                return true;
        }
        return false;
    }

    private static bool IsFlagChar(char c)
        => c == 'i' || c == 'm' || c == 's' || c == 'U' || c == 'x' || c == '-';
}
