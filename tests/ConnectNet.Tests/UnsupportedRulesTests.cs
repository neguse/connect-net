using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// Rules the handwritten evaluators used to refuse with NotSupportedException are evaluated by
/// the CEL engine now: custom expressions, Any, FieldMask and every string format.
/// </summary>
public class UnsupportedRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void FieldLevelCel_IsEvaluated()
    {
        var result = _validator.Validate(new UnsupportedFieldCelMessage { Name = "x" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MessageLevelCel_IsEvaluated()
    {
        var result = _validator.Validate(new UnsupportedMessageCelMessage { Name = "x" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void StringAddress_IsEvaluated()
    {
        Assert.True(_validator.Validate(new UnsupportedStringFormatMessage { Addr = "example.com" }).IsValid);
        Assert.True(_validator.Validate(new UnsupportedStringFormatMessage { Addr = "::1" }).IsValid);

        var result = _validator.Validate(new UnsupportedStringFormatMessage { Addr = "not an address" });

        Assert.Contains(result.Violations, v => v.FieldPath == "addr" && v.ConstraintId == "string.address");
    }

    [Fact]
    public void AnyRules_AreEvaluated()
    {
        var allowed = new UnsupportedAnyMessage { Value = new Any { TypeUrl = "type.googleapis.com/foo" } };
        Assert.True(_validator.Validate(allowed).IsValid);

        var result = _validator.Validate(new UnsupportedAnyMessage { Value = new Any { TypeUrl = "type.googleapis.com/bar" } });

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "value" && v.ConstraintId == "any.in" && v.Message == "type URL must be in the allow list");
    }

    [Fact]
    public void FieldMaskRules_AreEvaluated()
    {
        var mask = new FieldMask();
        mask.Paths.Add("name");
        Assert.True(_validator.Validate(new UnsupportedFieldMaskMessage { Mask = mask }).IsValid);

        mask.Paths.Add("secret");
        var result = _validator.Validate(new UnsupportedFieldMaskMessage { Mask = mask });

        Assert.Contains(result.Violations, v => v.FieldPath == "mask" && v.ConstraintId == "field_mask.not_in");
    }

    [Fact]
    public void NestedItemsRule_IsEvaluated()
    {
        var msg = new UnsupportedNestedItemsMessage();
        msg.Ids.Add("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        msg.Ids.Add("not-a-ulid");

        var result = _validator.Validate(msg);

        Assert.Single(result.Violations);
        Assert.Contains(result.Violations, v => v.FieldPath == "ids[1]" && v.ConstraintId == "string.ulid");
    }

    [Fact]
    public void IgnoreUnsupportedRules_IsRetainedAsAFlag()
    {
        var permissive = new ProtoValidator(ignoreUnsupportedRules: true);

        Assert.True(permissive.IgnoreUnsupportedRules);
        Assert.True(permissive.Validate(new UnsupportedFieldCelMessage { Name = "x" }).IsValid);
    }

    [Fact]
    public void SupportedRules_DoNotThrow()
    {
        var result = _validator.Validate(new StringTestMessage
        {
            Name = "Alice",
            Email = "alice@example.com",
            Code = "abc",
            PrefixVal = "pre_value",
            PatternVal = "lowercase",
        });

        Assert.True(result.IsValid);
    }
}
