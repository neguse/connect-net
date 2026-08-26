using System;
using ConnectNet.Validation.Internal;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// buf.validate defines string.pattern / bytes.pattern as RE2, so what a schema accepts here
/// must be what protovalidate-go accepts for the same .proto. These pin the places where .NET's
/// engine reads a pattern differently.
/// </summary>
public class Re2PatternTests
{
    private static bool Matches(string pattern, string value) => Re2Pattern.GetRegex(pattern).IsMatch(value);

    // --- `$` is end of input, not "end of input or just before a trailing newline" ---

    [Fact]
    public void DollarAnchor_RejectsTrailingNewline()
    {
        Assert.True(Matches("^[a-z]+$", "abc"));
        Assert.False(Matches("^[a-z]+$", "abc\n"));
    }

    [Fact]
    public void CaretAnchor_RejectsLeadingLine()
    {
        Assert.False(Matches("^[a-z]+$", "\nabc"));
    }

    [Fact]
    public void EscapedAnchors_StayLiteral()
    {
        Assert.True(Matches(@"^a\$$", "a$"));
        Assert.True(Matches(@"^\^a$", "^a"));
    }

    [Fact]
    public void AnchorsInsideCharacterClass_StayLiteral()
    {
        Assert.True(Matches("^[$^]+$", "$^"));
    }

    [Fact]
    public void MultilineFlag_KeepsLineAnchors()
    {
        // RE2 spells the flag the same way, and under it `^`/`$` really are line anchors.
        Assert.True(Matches("(?m)^b$", "a\nb"));
        Assert.True(Matches("(?m:^b$)", "a\nb"));
    }

    // --- shorthand classes are ASCII, as RE2 defines them ---

    [Fact]
    public void Digit_IsAsciiOnly()
    {
        Assert.True(Matches(@"^\d+$", "123"));
        Assert.False(Matches(@"^\d+$", "\u0661\u0662\u0663")); // Arabic-Indic digits
    }

    [Fact]
    public void Word_IsAsciiOnly()
    {
        Assert.True(Matches(@"^\w+$", "admin_1"));
        Assert.False(Matches(@"^\w+$", "\uFF41dmin")); // fullwidth 'a'
    }

    [Fact]
    public void Space_ExcludesVerticalTab()
    {
        Assert.True(Matches(@"^a\sb$", "a b"));
        Assert.True(Matches(@"^a\sb$", "a\tb"));
        Assert.False(Matches(@"^a\sb$", "a\vb"));
        Assert.False(Matches(@"^a\sb$", "a\u00A0b")); // no-break space
    }

    [Fact]
    public void NegatedShorthands_AreAsciiOnly()
    {
        Assert.True(Matches(@"^\D+$", "\u0661\u0662\u0663"));   // not ASCII digits, so \D matches
        Assert.True(Matches(@"^\W+$", "\uFF41")); // not an ASCII word character
        Assert.True(Matches(@"^\S+$", "\v"));     // not RE2 whitespace
    }

    [Fact]
    public void ShorthandInsideCharacterClass_IsAsciiOnly()
    {
        Assert.True(Matches(@"^[\d.]+$", "1.2"));
        Assert.False(Matches(@"^[\d.]+$", "\u0661.2"));
    }

    [Fact]
    public void NegatedShorthandInsideCharacterClass_IsRejected()
    {
        // Its ASCII meaning cannot be expressed as one .NET class; refuse rather than
        // compile something that means something else.
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex(@"^[\D.]+$"));
    }

    // --- POSIX classes exist in RE2 and mean nothing to .NET ---

    [Fact]
    public void PosixClass_IsTranslated()
    {
        Assert.True(Matches("^[[:alnum:]]+$", "abc123"));
        Assert.False(Matches("^[[:alnum:]]+$", "ab-c"));
        Assert.True(Matches("^[[:xdigit:]]+$", "dead99"));
        Assert.True(Matches("^[[:punct:][:space:]]+$", "!? \t"));
    }

    [Fact]
    public void PosixClass_MixedWithOtherMembers_IsTranslated()
    {
        Assert.True(Matches("^[[:digit:]abc]+$", "1a2b"));
        Assert.False(Matches("^[[:digit:]abc]+$", "1a2d"));
    }

    [Fact]
    public void NegatedPosixClass_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex("^[[:^alpha:]]+$"));
    }

    // --- \b is the ASCII word boundary ---

    [Fact]
    public void WordBoundary_IsAsciiOnly()
    {
        Assert.True(Matches(@"\bfoo\b", "say foo now"));
        Assert.False(Matches(@"\bfoo\b", "barfoo"));
        Assert.True(Matches(@"\bfoo\b", "\uFF41foo")); // fullwidth 'a' is not an ASCII word char
    }

    [Fact]
    public void NonWordBoundary_IsAsciiOnly()
    {
        Assert.True(Matches(@"bar\Bfoo", "barfoo"));
        Assert.False(Matches(@"bar\Bfoo", "bar foo"));
    }

    // --- ordinary patterns keep working ---

    [Fact]
    public void PlainPattern_StillMatches()
    {
        Assert.True(Matches(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$", "a.b@example.com"));
        Assert.True(Matches("colou?r", "color"));
        Assert.True(Matches("(foo|bar)+", "foobar"));
    }

    [Fact]
    public void InvalidPattern_ThrowsArgumentException()
    {
        // RegexParseException derives from ArgumentException, which is what the rule
        // evaluators catch to report the pattern itself as invalid.
        Assert.ThrowsAny<ArgumentException>(() => Re2Pattern.GetRegex("("));
    }

    [Fact]
    public void SamePattern_IsCompiledOnce()
    {
        Assert.Same(Re2Pattern.GetRegex("^[a-z]+$"), Re2Pattern.GetRegex("^[a-z]+$"));
    }

    // --- the `m` flag is tracked by scope, not detected by a pattern-wide text scan ---

    [Fact]
    public void ScopedMultilineGroup_DoesNotLeakPastItsGroup()
    {
        Assert.False(Matches("(?m:a)b$", "ab\n"));
        Assert.True(Matches("(?m:a)b$", "ab"));
    }

    [Fact]
    public void ClearedMultilineFlag_KeepsEndOfTextAnchor()
    {
        Assert.False(Matches("(?i-m)^a$", "a\n"));
        Assert.True(Matches("(?i-m)^a$", "A"));
    }

    [Fact]
    public void FlagGroupAfterTheAnchor_DoesNotAffectIt()
    {
        Assert.False(Matches("^a$(?m)", "a\n"));
        Assert.True(Matches("^a$(?m)", "a"));
    }

    [Fact]
    public void FlagLikeTextInsideCharacterClass_IsNotAFlag()
    {
        Assert.False(Matches("^[a-z(?m)]+$", "abc\n"));
        Assert.True(Matches("^[a-z(?m)]+$", "abc(?m)"));
    }

    [Fact]
    public void MultilineFlag_EndsWithItsEnclosingGroup()
    {
        Assert.False(Matches("((?m)a)b$", "ab\n"));
        Assert.True(Matches("((?m)a)b$", "ab"));
    }

    // --- '-' inside a class: ranges form only between two single characters ---

    [Fact]
    public void DashAfterShorthandClass_IsLiteral()
    {
        Assert.True(Matches(@"^[\w-~]+$", "a-~_"));
        Assert.False(Matches(@"^[\w-~]+$", "|"));
        Assert.False(Matches(@"^[\w-~]+$", "{"));
        Assert.False(Matches(@"^[\w-~]+$", "`"));
    }

    [Fact]
    public void DashAfterShorthandClass_BeforeSpace_IsLiteral()
    {
        // Handed to .NET verbatim this class is a reversed range and rejects every value.
        Assert.True(Matches(@"^[\w- ]+$", "a b-c"));
        Assert.False(Matches(@"^[\w- ]+$", "|"));
    }

    [Fact]
    public void DashAfterPosixClass_IsLiteral()
    {
        Assert.True(Matches("^[[:blank:]-~]+$", "\t -~"));
        Assert.False(Matches("^[[:blank:]-~]+$", "0"));
        Assert.False(Matches("^[[:space:]-~]+$", "0"));
        Assert.False(Matches(@"^[[:cntrl:]-\xFF]+$", "\u0090"));
        Assert.True(Matches(@"^[[:cntrl:]-\xFF]+$", "-\u00FF\t"));
    }

    [Fact]
    public void ClassSubtractionSyntax_HasNoSpecialMeaning()
    {
        // .NET reads -[...] as class subtraction; RE2 ends the class at the first ']'.
        Assert.True(Matches("^[a-c-[b]]$", "a]"));
        Assert.True(Matches("^[a-c-[b]]$", "b]"));
        Assert.False(Matches("^[a-c-[b]]$", "a"));
    }

    [Fact]
    public void ReversedRange_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex("^[z-a]$"));
    }

    [Fact]
    public void RangeWithClassEndpoint_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex(@"^[a-\d]$"));
    }

    // --- RE2-only spellings are translated, not left to fail or change meaning ---

    [Fact]
    public void OctalEscape_IsACharacterCode_NotABackreference()
    {
        Assert.True(Matches(@"^(a)\1$", "a\x01"));
        Assert.False(Matches(@"^(a)\1$", "aa"));
    }

    [Fact]
    public void BracedHexEscape_IsTranslated()
    {
        Assert.True(Matches(@"^\x{61}$", "a"));
        Assert.False(Matches(@"^\x{61}$", "b"));
        Assert.True(Matches(@"^\x{1F600}$", "\U0001F600"));
    }

    [Fact]
    public void QuotedLiteral_IsTranslated()
    {
        Assert.True(Matches(@"^\Qa.b\E$", "a.b"));
        Assert.False(Matches(@"^\Qa.b\E$", "axb"));
        Assert.True(Matches(@"^\Qa+\E$", "a+"));
    }

    [Fact]
    public void NamedGroup_Re2Spelling_IsTranslated()
    {
        Assert.True(Matches("^(?P<x>a)b$", "ab"));
        Assert.True(Matches("^(?<x>a)b$", "ab"));
    }

    // --- constructs RE2 rejects are rejected, not compiled with .NET meanings ---

    [Theory]
    [InlineData("^(?=a)a$")]     // lookahead
    [InlineData("^(?!b)a$")]     // negative lookahead
    [InlineData("^(?<=a)b$")]    // lookbehind
    [InlineData("^(?<!a)b$")]    // negative lookbehind
    [InlineData("^a(?#note)b$")] // comment group
    [InlineData(@"^a\Z")]        // \Z is .NET-only
    [InlineData(@"^[\b]$")]      // backspace class member is .NET-only
    [InlineData(@"^a\8$")]       // \8 is not an octal escape
    [InlineData("^(?xi)a$")]     // x is not an RE2 flag
    public void NetOnlyConstructs_AreRejected(string pattern)
    {
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex(pattern));
    }

    [Fact]
    public void InvalidPattern_FailsTheSameWayWhenAskedTwice()
    {
        // Failures are cached like successes; the second lookup must not bypass the error.
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex(@"^\x{$"));
        Assert.Throws<ArgumentException>(() => Re2Pattern.GetRegex(@"^\x{$"));
    }
}
