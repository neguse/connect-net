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
}
