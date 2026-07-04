using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class CollectionRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- enum defined_only inside repeated items ---

    [Fact]
    public void RepeatedItems_EnumDefinedOnly_Undefined_Violation()
    {
        var msg = new CollectionEnumTestMessage();
        msg.Statuses.Add((Status)999);

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "statuses[0]" && v.ConstraintId == "enum.defined_only");
    }

    [Fact]
    public void RepeatedItems_EnumDefinedOnly_Defined_NoViolation()
    {
        var msg = new CollectionEnumTestMessage();
        msg.Statuses.Add(Status.Active);

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    // --- enum defined_only inside map values ---

    [Fact]
    public void MapValues_EnumDefinedOnly_Undefined_Violation()
    {
        var msg = new CollectionEnumTestMessage();
        msg.StatusMap.Add(1, (Status)999);

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "statusMap[1]" && v.ConstraintId == "enum.defined_only");
    }

    [Fact]
    public void MapValues_EnumDefinedOnly_Defined_NoViolation()
    {
        var msg = new CollectionEnumTestMessage();
        msg.StatusMap.Add(1, Status.Inactive);

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    // --- IGNORE_IF_ZERO_VALUE on repeated / map ---

    [Fact]
    public void IgnoreIfZero_EmptyRepeated_Skipped()
    {
        var msg = new IgnoreZeroCollectionsMessage();

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void IgnoreIfZero_NonEmptyRepeated_RulesApply()
    {
        var msg = new IgnoreZeroCollectionsMessage();
        msg.Tags.Add("one"); // min_items = 2

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "tags" && v.ConstraintId == "repeated.min_items");
    }

    [Fact]
    public void IgnoreIfZero_NonEmptyMap_RulesApply()
    {
        var msg = new IgnoreZeroCollectionsMessage();
        msg.Labels.Add("k", "v"); // min_pairs = 2

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "labels" && v.ConstraintId == "map.min_pairs");
    }

    // --- repeated.unique: NaN is never equal to itself ---

    [Fact]
    public void Unique_FloatNaN_NotCountedAsDuplicate()
    {
        var msg = new UniqueFloatTestMessage();
        msg.FloatValues.AddRange(new[] { float.NaN, float.NaN });

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.unique");
    }

    [Fact]
    public void Unique_DoubleNaN_NotCountedAsDuplicate()
    {
        var msg = new UniqueFloatTestMessage();
        msg.DoubleValues.AddRange(new[] { double.NaN, double.NaN });

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.unique");
    }

    [Fact]
    public void Unique_FloatDuplicate_Violation()
    {
        var msg = new UniqueFloatTestMessage();
        msg.FloatValues.AddRange(new[] { 1.5f, 1.5f });

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "floatValues" && v.ConstraintId == "repeated.unique");
    }
}
