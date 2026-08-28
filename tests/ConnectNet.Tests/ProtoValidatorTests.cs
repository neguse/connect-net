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

    // --- the request must not choose how many violations the server materializes ---

    [Fact]
    public void Validate_ManyFailingItems_StopsAtTheViolationLimit()
    {
        var msg = new RepeatedItemsTestMessage();
        for (int i = 0; i < 5000; i++) msg.Values.Add("x"); // each fails items.string.min_len = 3

        var result = _validator.Validate(msg);

        Assert.False(result.IsValid);
        Assert.Equal(ProtoValidator.DefaultMaxViolations, result.Violations.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Validate_ManyFailingNestedMessages_StopsAtTheViolationLimit()
    {
        var msg = new NestedTestMessage();
        for (int i = 0; i < 5000; i++) msg.Items.Add(new StringTestMessage { Name = "Al", Email = "bad" });

        var result = _validator.Validate(msg);

        Assert.Equal(ProtoValidator.DefaultMaxViolations, result.Violations.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Validate_ManyFailingMapEntries_StopsAtTheViolationLimit()
    {
        var msg = new MapKeysValuesTestMessage();
        for (int i = 0; i < 5000; i++) msg.Entries.Add($"k{i}", "x"); // values fail min_len = 3

        var result = _validator.Validate(msg);

        Assert.Equal(ProtoValidator.DefaultMaxViolations, result.Violations.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Validate_BelowTheLimit_ReportsEveryViolationAndIsNotTruncated()
    {
        var msg = new RepeatedItemsTestMessage();
        for (int i = 0; i < 3; i++) msg.Values.Add("x");

        var result = _validator.Validate(msg);

        Assert.Equal(3, result.Violations.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Validate_CustomViolationLimit_IsHonoured()
    {
        var validator = new ProtoValidator(ignoreUnsupportedRules: false, maxViolations: 5);
        var msg = new RepeatedItemsTestMessage();
        for (int i = 0; i < 100; i++) msg.Values.Add("x");

        var result = validator.Validate(msg);

        Assert.Equal(5, result.Violations.Count);
        Assert.True(result.Truncated);
    }
}
