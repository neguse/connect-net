using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class OneofRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Oneof_Required_Valid_EmailSet()
    {
        var message = new OneofTestMessage { Email = "test@example.com" };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Oneof_Required_Valid_PhoneSet()
    {
        var message = new OneofTestMessage { Phone = "123-456-7890" };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Oneof_Required_Invalid_NoneSet()
    {
        var message = new OneofTestMessage();

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "contact" &&
            v.ConstraintId == "oneof.required");
    }

    [Fact]
    public void NestedMessage_Required_Valid()
    {
        var message = new NestedTestMessage
        {
            Inner = new StringTestMessage
            {
                Name = "Alice",
                Email = "alice@example.com",
                Code = "abc",
                PrefixVal = "pre_value",
                PatternVal = "lowercase",
            }
        };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void NestedMessage_Required_Invalid()
    {
        var message = new NestedTestMessage();

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "inner" &&
            v.ConstraintId == "required");
    }

    [Fact]
    public void NestedMessage_RecursiveValidation()
    {
        var message = new NestedTestMessage
        {
            Inner = new StringTestMessage
            {
                Name = "Al", // min_len=3, too short
                Email = "alice@example.com",
                Code = "abc",
                PrefixVal = "pre_value",
                PatternVal = "lowercase",
            }
        };

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "inner.name" &&
            v.ConstraintId == "string.min_len");
    }
}
