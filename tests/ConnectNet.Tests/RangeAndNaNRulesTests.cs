using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class RangeAndNaNRulesTests
{
    private readonly ProtoValidator _validator = new();

    private static RangeTestMessage Valid() => new()
    {
        Outside = 12,     // gt:10, lt:5 → outside [5,10]
        Inside = 7,       // gte:5, lte:10
        FloatLt = 1f,
        DoubleGte = 1d,
        FloatFinite = 1f,
        DoubleFinite = 1d,
        FloatIn = 1.5f,
        FloatConst = 0f,
    };

    // --- reversed range: gt > lt means "this > gt OR this < lt" ---

    [Fact]
    public void ReversedRange_AboveGt_NoViolation()
    {
        var msg = Valid();
        msg.Outside = 12;

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "outside");
    }

    [Fact]
    public void ReversedRange_BelowLt_NoViolation()
    {
        var msg = Valid();
        msg.Outside = 3;

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "outside");
    }

    [Fact]
    public void ReversedRange_InsideForbiddenBand_Violation()
    {
        var msg = Valid();
        msg.Outside = 7; // within [5, 10] → forbidden

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "outside" && v.ConstraintId == "int32.gt_lt_exclusive");
    }

    [Fact]
    public void ReversedRange_Boundary_Violation()
    {
        // gt:10, lt:5 → 10 itself is not > 10, so it violates
        var msg = Valid();
        msg.Outside = 10;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v => v.FieldPath == "outside");
    }

    // --- normal range: gte <= lte behaves as AND ---

    [Fact]
    public void NormalRange_Inside_NoViolation()
    {
        var msg = Valid();
        msg.Inside = 5;

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "inside");
    }

    [Fact]
    public void NormalRange_Below_Violation()
    {
        var msg = Valid();
        msg.Inside = 4;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "inside" && v.ConstraintId == "int32.gte_lte");
    }

    [Fact]
    public void NormalRange_Above_Violation()
    {
        var msg = Valid();
        msg.Inside = 11;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "inside" && v.ConstraintId == "int32.gte_lte");
    }

    // --- NaN always violates comparison rules ---

    [Fact]
    public void NaN_FloatLt_Violation()
    {
        var msg = Valid();
        msg.FloatLt = float.NaN;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "floatLt" && v.ConstraintId == "float.lt");
    }

    [Fact]
    public void NaN_DoubleGte_Violation()
    {
        var msg = Valid();
        msg.DoubleGte = double.NaN;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "doubleGte" && v.ConstraintId == "double.gte");
    }

    [Fact]
    public void NaN_FloatIn_Violation()
    {
        var msg = Valid();
        msg.FloatIn = float.NaN;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "floatIn" && v.ConstraintId == "float.in");
    }

    [Fact]
    public void NaN_FloatConst_Violation()
    {
        var msg = Valid();
        msg.FloatConst = float.NaN;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "floatConst" && v.ConstraintId == "float.const");
    }

    // --- finite ---

    [Fact]
    public void Finite_NaN_Violation()
    {
        var msg = Valid();
        msg.FloatFinite = float.NaN;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "floatFinite" && v.ConstraintId == "float.finite");
    }

    [Fact]
    public void Finite_Infinity_Violation()
    {
        var msg = Valid();
        msg.DoubleFinite = double.PositiveInfinity;

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "doubleFinite" && v.ConstraintId == "double.finite");
    }

    [Fact]
    public void Finite_FiniteValue_NoViolation()
    {
        var msg = Valid();

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }
}
