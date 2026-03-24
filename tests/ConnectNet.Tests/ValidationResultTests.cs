using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class ValidationResultTests
{
    [Fact]
    public void Success_IsValid()
    {
        Assert.True(ValidationResult.Success.IsValid);
        Assert.Empty(ValidationResult.Success.Violations);
    }

    [Fact]
    public void Fail_WithViolations_IsNotValid()
    {
        var result = ValidationResult.Fail(new[]
        {
            new Violation("name", "string.min_len", "value length must be at least 3", "ab")
        });

        Assert.False(result.IsValid);
        Assert.Single(result.Violations);
        Assert.Equal("name", result.Violations[0].FieldPath);
        Assert.Equal("string.min_len", result.Violations[0].ConstraintId);
        Assert.Equal("value length must be at least 3", result.Violations[0].Message);
        Assert.Equal("ab", result.Violations[0].Value);
    }

    [Fact]
    public void Fail_WithEmptyViolations_IsValid()
    {
        var result = ValidationResult.Fail(System.Array.Empty<Violation>());
        Assert.True(result.IsValid);
    }
}
