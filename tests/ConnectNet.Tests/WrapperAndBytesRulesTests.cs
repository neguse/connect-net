using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf;
using Xunit;

namespace ConnectNet.Tests;

public class WrapperAndBytesRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- wrapper types are unwrapped and validated with scalar rules ---

    [Fact]
    public void Wrapper_Unset_NoViolation()
    {
        var msg = new WrapperTestMessage();
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Wrapper_StringValue_Invalid_Violation()
    {
        var msg = new WrapperTestMessage { Name = "ab" }; // min_len 3
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "name" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void Wrapper_StringValue_Valid_NoViolation()
    {
        var msg = new WrapperTestMessage { Name = "abc" };
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Wrapper_Int32Value_Invalid_Violation()
    {
        var msg = new WrapperTestMessage { Num = 5 }; // gt 10
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "num" && v.ConstraintId == "int32.gt");
    }

    [Fact]
    public void Wrapper_BoolValue_Invalid_Violation()
    {
        var msg = new WrapperTestMessage { Flag = false }; // const true
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "flag" && v.ConstraintId == "bool.const");
    }

    [Fact]
    public void Wrapper_DoubleValue_Invalid_Violation()
    {
        var msg = new WrapperTestMessage { Score = 150 }; // lt 100
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "score" && v.ConstraintId == "double.lt");
    }

    // --- bytes prefix/suffix/contains/pattern ---

    private static ByteString B(params byte[] bytes) => ByteString.CopyFrom(bytes);

    [Fact]
    public void BytesPrefix_Match_NoViolation()
    {
        var msg = new BytesExtraTestMessage { PrefixVal = B(0x01, 0x02, 0x99) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "prefixVal");
    }

    [Fact]
    public void BytesPrefix_Mismatch_Violation()
    {
        var msg = new BytesExtraTestMessage { PrefixVal = B(0x01, 0x03) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "prefixVal" && v.ConstraintId == "bytes.prefix");
    }

    [Fact]
    public void BytesSuffix_Match_NoViolation()
    {
        var msg = new BytesExtraTestMessage { SuffixVal = B(0x01, 0xFF) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "suffixVal");
    }

    [Fact]
    public void BytesSuffix_Mismatch_Violation()
    {
        var msg = new BytesExtraTestMessage { SuffixVal = B(0x01, 0x02) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "suffixVal" && v.ConstraintId == "bytes.suffix");
    }

    [Fact]
    public void BytesContains_Match_NoViolation()
    {
        var msg = new BytesExtraTestMessage { ContainsVal = ByteString.CopyFromUtf8("xxabcxx") };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "containsVal");
    }

    [Fact]
    public void BytesContains_Missing_Violation()
    {
        var msg = new BytesExtraTestMessage { ContainsVal = ByteString.CopyFromUtf8("xxx") };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "containsVal" && v.ConstraintId == "bytes.contains");
    }

    [Fact]
    public void BytesPattern_Match_NoViolation()
    {
        var msg = new BytesExtraTestMessage { PatternVal = ByteString.CopyFromUtf8("abc") };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "patternVal");
    }

    [Fact]
    public void BytesPattern_Mismatch_Violation()
    {
        var msg = new BytesExtraTestMessage { PatternVal = ByteString.CopyFromUtf8("ABC") };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "patternVal" && v.ConstraintId == "bytes.pattern");
    }

    [Fact]
    public void BytesPattern_InvalidUtf8_Violation()
    {
        var msg = new BytesExtraTestMessage { PatternVal = B(0xFF, 0xFE) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "patternVal" && v.ConstraintId == "bytes.pattern");
    }

    [Fact]
    public void BytesPattern_TrailingNewline_Violation()
    {
        // RE2's `$` is end of input, so `^[a-z]+$` does not accept a trailing 0x0A.
        var msg = new BytesExtraTestMessage { PatternVal = B(0x61, 0x62, 0x63, 0x0A) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "patternVal" && v.ConstraintId == "bytes.pattern");
    }
}
