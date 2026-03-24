using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class ProtoValidatorTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Validate_ValidMessage_ReturnsSuccess()
    {
        var message = new StringTestMessage
        {
            Name = "Alice",
            Email = "alice@example.com",
            Code = "abc",
            PrefixVal = "pre_value",
            PatternVal = "lowercase",
        };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Validate_InvalidMessage_ReturnsViolations()
    {
        var message = new StringTestMessage
        {
            Name = "Al", // min_len=3, too short
            Email = "alice@example.com",
            Code = "abc",
            PrefixVal = "pre_value",
            PatternVal = "lowercase",
        };

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "name" &&
            v.ConstraintId == "string.min_len" &&
            v.Message.Contains("3"));
    }

    [Fact]
    public void Validate_MessageWithoutConstraints_ReturnsSuccess()
    {
        var message = new HelloRequest
        {
            Name = "anything",
        };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Validate_IgnoreAlways_SkipsValidation()
    {
        // DisabledFieldMessage has skip_me with IGNORE_ALWAYS and min_len=5
        var msg = new DisabledFieldMessage
        {
            Name = "Alice",    // valid (min_len=3)
            SkipMe = "ab"      // would violate min_len=5 but should be skipped
        };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "skipMe");
    }

    [Fact]
    public void Validate_IgnoreAlways_WouldFailWithoutIgnore()
    {
        var msg = new DisabledFieldMessage
        {
            Name = "Al",     // min_len=3, should FAIL
            SkipMe = "ab"    // min_len=5, should NOT fail (ignored)
        };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "name");
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "skipMe");
    }

    [Fact]
    public void Validate_NestedMessage_CollectsAllViolations()
    {
        var msg = new NestedTestMessage
        {
            Inner = new StringTestMessage { Name = "Al", Email = "bad" }
        };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "inner.name");
        Assert.Contains(result.Violations, v => v.FieldPath == "inner.email");
    }
}
