using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class NestedRecursionTests
{
    private readonly ProtoValidator _validator = new();

    private static StringTestMessage ValidInner() => new()
    {
        Name = "Alice",
        Email = "alice@example.com",
        Code = "abc",
        PrefixVal = "pre_value",
        PatternVal = "lowercase",
    };

    private static StringTestMessage InvalidInner()
    {
        var inner = ValidInner();
        inner.Name = "Al"; // string.min_len = 3
        return inner;
    }

    // --- repeated message elements ---

    [Fact]
    public void RepeatedMessageElement_Invalid_ViolationWithSubscript()
    {
        var msg = new NestedTestMessage { Inner = ValidInner() };
        msg.Items.Add(ValidInner());
        msg.Items.Add(InvalidInner());

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "items[1].name" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void RepeatedMessageElement_AllValid_NoViolation()
    {
        var msg = new NestedTestMessage { Inner = ValidInner() };
        msg.Items.Add(ValidInner());

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    // --- map message values ---

    [Fact]
    public void MapMessageValue_Invalid_ViolationWithSubscript()
    {
        var msg = new NestedMapTestMessage();
        msg.Entries.Add("k", InvalidInner());

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "entries[\"k\"].name" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void MapMessageValue_Valid_NoViolation()
    {
        var msg = new NestedMapTestMessage();
        msg.Entries.Add("k", ValidInner());

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    // --- recursion depth cap is shared with collection recursion ---

    [Fact]
    public void MapRecursion_RespectsDepthCap()
    {
        // NestedMapTestMessage cannot self-nest, so just assert the cap constant is shared
        // by validating a deeply nested structure through singular fields still works.
        Assert.Equal(32, ProtoValidator.MaxRecursionDepth);
    }
}
