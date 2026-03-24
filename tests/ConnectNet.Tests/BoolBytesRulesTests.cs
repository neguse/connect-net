using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class BoolBytesRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- Bool const ---

    [Fact]
    public void BoolConst_True_NoViolation()
    {
        var msg = new BoolTestMessage { MustBeTrue = true };

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "bool.const");
    }

    [Fact]
    public void BoolConst_False_Violation()
    {
        var msg = new BoolTestMessage { MustBeTrue = false };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "mustBeTrue" &&
            v.ConstraintId == "bool.const" &&
            v.Message.Contains("true"));
    }
}
