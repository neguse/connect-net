using System;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class UnsupportedRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void FieldLevelCel_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedFieldCelMessage { Name = "x" }));
        Assert.Contains("name", ex.Message);
        Assert.Contains("cel", ex.Message);
    }

    [Fact]
    public void MessageLevelCel_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedMessageCelMessage { Name = "x" }));
        Assert.Contains("cel", ex.Message);
    }

    [Fact]
    public void StringAddress_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedStringFormatMessage { Addr = "x" }));
        Assert.Contains("addr", ex.Message);
        Assert.Contains("address", ex.Message);
    }

    [Fact]
    public void AnyRules_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedAnyMessage()));
        Assert.Contains("any", ex.Message);
    }

    [Fact]
    public void FieldMaskRules_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedFieldMaskMessage()));
        Assert.Contains("field_mask", ex.Message);
    }

    [Fact]
    public void NestedItemsUnsupportedRule_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _validator.Validate(new UnsupportedNestedItemsMessage()));
        Assert.Contains("ulid", ex.Message);
    }

    [Fact]
    public void OptOut_IgnoreUnsupportedRules_DoesNotThrow()
    {
        var permissive = new ProtoValidator(ignoreUnsupportedRules: true);

        var result = permissive.Validate(new UnsupportedFieldCelMessage { Name = "x" });

        Assert.True(result.IsValid);
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
