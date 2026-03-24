using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class StringRulesTests
{
    private readonly ProtoValidator _validator = new();

    private static StringTestMessage ValidMessage() => new()
    {
        Name = "Alice",
        Email = "alice@example.com",
        Code = "abc",
        PrefixVal = "pre_value",
        PatternVal = "lowercase",
    };

    // --- min_len ---

    [Fact]
    public void MinLen_Valid_NoViolation()
    {
        var msg = ValidMessage();
        msg.Name = "Alice"; // length 5 >= 3

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.min_len" && v.FieldPath == "name");
    }

    [Fact]
    public void MinLen_TooShort_Violation()
    {
        var msg = ValidMessage();
        msg.Name = "Al"; // length 2 < 3

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "name" &&
            v.ConstraintId == "string.min_len" &&
            v.Message.Contains("3"));
    }

    [Fact]
    public void MinLen_ExactBoundary_NoViolation()
    {
        var msg = ValidMessage();
        msg.Name = "Bob"; // length 3 == 3

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.min_len" && v.FieldPath == "name");
    }

    // --- max_len ---

    [Fact]
    public void MaxLen_Valid_NoViolation()
    {
        var msg = ValidMessage();
        msg.Code = "short"; // length 5 <= 10

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.max_len" && v.FieldPath == "code");
    }

    [Fact]
    public void MaxLen_TooLong_Violation()
    {
        var msg = ValidMessage();
        msg.Code = "this_is_way_too_long"; // length > 10

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "code" &&
            v.ConstraintId == "string.max_len" &&
            v.Message.Contains("10"));
    }

    [Fact]
    public void MaxLen_ExactBoundary_NoViolation()
    {
        var msg = ValidMessage();
        msg.Code = "exactly_10"; // length 10 == 10

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.max_len" && v.FieldPath == "code");
    }

    // --- email ---

    [Fact]
    public void Email_Valid_NoViolation()
    {
        var msg = ValidMessage();
        msg.Email = "user@example.com";

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.email");
    }

    [Fact]
    public void Email_Invalid_MissingAt_Violation()
    {
        var msg = ValidMessage();
        msg.Email = "not-an-email";

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "email" &&
            v.ConstraintId == "string.email");
    }

    [Fact]
    public void Email_Invalid_Empty_Violation()
    {
        var msg = ValidMessage();
        msg.Email = "";

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "email" &&
            v.ConstraintId == "string.email");
    }

    // --- prefix ---

    [Fact]
    public void Prefix_Valid_NoViolation()
    {
        var msg = ValidMessage();
        msg.PrefixVal = "pre_hello";

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.prefix");
    }

    [Fact]
    public void Prefix_Invalid_Violation()
    {
        var msg = ValidMessage();
        msg.PrefixVal = "nopre_hello";

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "prefixVal" &&
            v.ConstraintId == "string.prefix" &&
            v.Message.Contains("pre_"));
    }

    // --- pattern ---

    [Fact]
    public void Pattern_Valid_NoViolation()
    {
        var msg = ValidMessage();
        msg.PatternVal = "lowercase";

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.pattern");
    }

    [Fact]
    public void Pattern_Invalid_Violation()
    {
        var msg = ValidMessage();
        msg.PatternVal = "UPPERCASE";

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "patternVal" &&
            v.ConstraintId == "string.pattern");
    }

    [Fact]
    public void Pattern_Invalid_WithNumbers_Violation()
    {
        var msg = ValidMessage();
        msg.PatternVal = "has123numbers";

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "patternVal" &&
            v.ConstraintId == "string.pattern");
    }

    // --- combined rules ---

    [Fact]
    public void MultipleViolations_AllReported()
    {
        var msg = new StringTestMessage
        {
            Name = "Al",                    // min_len=3 violation
            Email = "bad",                  // email violation
            Code = "x",                     // min_len=2 violation
            PrefixVal = "no_prefix",        // prefix violation
            PatternVal = "UPPER123",        // pattern violation
        };

        var result = _validator.Validate(msg);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.FieldPath == "name" && v.ConstraintId == "string.min_len");
        Assert.Contains(result.Violations, v => v.FieldPath == "email" && v.ConstraintId == "string.email");
        Assert.Contains(result.Violations, v => v.FieldPath == "code" && v.ConstraintId == "string.min_len");
        Assert.Contains(result.Violations, v => v.FieldPath == "prefixVal" && v.ConstraintId == "string.prefix");
        Assert.Contains(result.Violations, v => v.FieldPath == "patternVal" && v.ConstraintId == "string.pattern");
    }

    [Fact]
    public void AllValid_NoViolations()
    {
        var msg = ValidMessage();

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }
}
