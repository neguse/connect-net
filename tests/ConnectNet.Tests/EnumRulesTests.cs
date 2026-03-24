using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class EnumRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- defined_only ---

    [Fact]
    public void DefinedOnly_ValidValue_NoViolation()
    {
        var msg = new EnumTestMessage { Status = Status.Active };

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "enum.defined_only");
    }

    [Fact]
    public void DefinedOnly_UnspecifiedValue_NoViolation()
    {
        var msg = new EnumTestMessage { Status = Status.Unspecified };

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "enum.defined_only");
    }

    [Fact]
    public void DefinedOnly_UndefinedValue_Violation()
    {
        var msg = new EnumTestMessage { Status = (Status)999 };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "status" &&
            v.ConstraintId == "enum.defined_only");
    }

    // --- const ---

    [Fact]
    public void Const_Valid()
    {
        var msg = new EnumConstTestMessage { Status = Status.Active }; // Active = 1, const = 1

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Const_Invalid()
    {
        var msg = new EnumConstTestMessage { Status = Status.Inactive }; // Inactive = 2, const = 1

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "status" &&
            v.ConstraintId == "enum.const");
    }

    [Fact]
    public void Const_Unspecified_Invalid()
    {
        var msg = new EnumConstTestMessage { Status = Status.Unspecified }; // 0 != 1

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "status" &&
            v.ConstraintId == "enum.const");
    }
}
