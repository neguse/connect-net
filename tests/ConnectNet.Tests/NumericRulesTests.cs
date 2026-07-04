using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class NumericRulesTests
{
    private readonly ProtoValidator _validator = new();

    private static NumericTestMessage ValidMessage() => new()
    {
        Age = 25,
        Score = 85.0,
        Count = 10,
    };

    // --- Int32 gte ---

    [Fact]
    public void Int32_Gte_Valid()
    {
        var msg = ValidMessage();
        msg.Age = 0; // boundary: gte=0

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "int32.gte" && v.FieldPath == "age");
    }

    [Fact]
    public void Int32_Gte_Invalid()
    {
        var msg = ValidMessage();
        msg.Age = -1; // less than 0

        var result = _validator.Validate(msg);

        // age declares both gte and lte, so protovalidate reports the combined rule id.
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "age" &&
            v.ConstraintId == "int32.gte_lte");
    }

    // --- Int32 lte ---

    [Fact]
    public void Int32_Lte_Invalid()
    {
        var msg = ValidMessage();
        msg.Age = 151; // greater than 150

        var result = _validator.Validate(msg);

        // age declares both gte and lte, so protovalidate reports the combined rule id.
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "age" &&
            v.ConstraintId == "int32.gte_lte");
    }

    // --- Double range ---

    [Fact]
    public void Double_Range_Valid()
    {
        var msg = ValidMessage();
        msg.Score = 50.0;

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "score");
    }

    [Fact]
    public void Double_Range_Invalid()
    {
        var msg = ValidMessage();
        msg.Score = 100.1; // greater than 100.0

        var result = _validator.Validate(msg);

        // score declares both gte and lte, so protovalidate reports the combined rule id.
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "score" &&
            v.ConstraintId == "double.gte_lte");
    }

    // --- Uint64 gt ---

    [Fact]
    public void Uint64_Gt_Invalid()
    {
        var msg = ValidMessage();
        msg.Count = 0; // not greater than 0

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "count" &&
            v.ConstraintId == "uint64.gt");
    }
}
