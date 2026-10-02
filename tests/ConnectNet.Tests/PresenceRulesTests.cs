using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class PresenceRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- proto3 optional: rules ignored when unset ---

    [Fact]
    public void OptionalField_Unset_RulesIgnored()
    {
        var msg = new PresenceTestMessage();

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void OptionalField_SetInvalid_Violation()
    {
        var msg = new PresenceTestMessage { OptName = "ab" };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "opt_name" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void OptionalField_SetToEmpty_RulesApply()
    {
        // Explicitly set to "" — presence tracked, so rules apply to the empty string.
        var msg = new PresenceTestMessage { OptName = "" };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "opt_name" && v.ConstraintId == "string.min_len");
    }

    // --- oneof members: rules ignored when the member is not selected ---

    [Fact]
    public void OneofMember_NotSelected_RulesIgnored()
    {
        var msg = new PresenceTestMessage { ChoiceB = 20 }; // choice_a not selected

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "choice_a");
    }

    [Fact]
    public void OneofMember_Selected_RulesApply()
    {
        var msg = new PresenceTestMessage { ChoiceA = "ab" };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "choice_a" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void OneofMember_SelectedInvalidInt_Violation()
    {
        var msg = new PresenceTestMessage { ChoiceB = 5 };

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "choice_b" && v.ConstraintId == "int32.gt");
    }

    // --- required: presence-tracking fields must be set ---

    private static RequiredTestMessage ValidRequired() => new()
    {
        OptRequired = "",
        ImplicitRequired = "x",
        NumRequired = 1,
    };

    [Fact]
    public void Required_OptionalUnset_Violation()
    {
        var msg = ValidRequired();
        msg.ClearOptRequired();
        msg.ListRequired.Add("a");

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "opt_required" && v.ConstraintId == "required");
    }

    [Fact]
    public void Required_OptionalSetToEmpty_NoViolation()
    {
        // For presence-tracking fields, required only demands that the field is set;
        // the empty string is a valid value.
        var msg = ValidRequired();
        msg.ListRequired.Add("a");

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    // --- required: implicit-presence fields must be non-zero ---

    [Fact]
    public void Required_ImplicitString_Zero_Violation()
    {
        var msg = ValidRequired();
        msg.ImplicitRequired = "";
        msg.ListRequired.Add("a");

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "implicit_required" && v.ConstraintId == "required");
    }

    [Fact]
    public void Required_ImplicitInt_Zero_Violation()
    {
        var msg = ValidRequired();
        msg.NumRequired = 0;
        msg.ListRequired.Add("a");

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "num_required" && v.ConstraintId == "required");
    }

    [Fact]
    public void Required_RepeatedEmpty_Violation()
    {
        var msg = ValidRequired();

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "list_required" && v.ConstraintId == "required");
    }

    [Fact]
    public void Required_RepeatedNonEmpty_NoViolation()
    {
        var msg = ValidRequired();
        msg.ListRequired.Add("a");

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "list_required");
    }
}
