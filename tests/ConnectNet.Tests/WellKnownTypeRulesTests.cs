using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

public class WellKnownTypeRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Timestamp_Required_Valid()
    {
        var message = new TimestampTestMessage
        {
            CreatedAt = Timestamp.FromDateTime(System.DateTime.UtcNow)
        };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Timestamp_Required_Invalid()
    {
        var message = new TimestampTestMessage();

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "createdAt" &&
            v.ConstraintId == "required");
    }

    [Fact]
    public void Duration_Required_Valid()
    {
        var message = new DurationTestMessage
        {
            Timeout = Duration.FromTimeSpan(System.TimeSpan.FromSeconds(30))
        };

        var result = _validator.Validate(message);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Duration_Required_Invalid()
    {
        var message = new DurationTestMessage();

        var result = _validator.Validate(message);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "timeout" &&
            v.ConstraintId == "required");
    }
}
