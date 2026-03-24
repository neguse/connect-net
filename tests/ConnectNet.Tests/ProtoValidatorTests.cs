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
}
