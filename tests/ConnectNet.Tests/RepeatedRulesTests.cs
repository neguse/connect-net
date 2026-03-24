using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class RepeatedRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- min_items ---

    [Fact]
    public void MinItems_Valid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("tag1");

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.min_items");
    }

    [Fact]
    public void MinItems_Invalid()
    {
        var msg = new RepeatedTestMessage();
        // tags is empty, min_items=1

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "tags" &&
            v.ConstraintId == "repeated.min_items");
    }

    // --- max_items ---

    [Fact]
    public void MaxItems_Invalid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.AddRange(new[] { "a", "b", "c", "d", "e", "f" }); // 6 items, max_items=5

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "tags" &&
            v.ConstraintId == "repeated.max_items");
    }

    // --- unique ---

    [Fact]
    public void Unique_Valid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("tag1"); // satisfy min_items
        msg.Scores.AddRange(new[] { 1, 2, 3 });

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.unique");
    }

    [Fact]
    public void Unique_Invalid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("tag1"); // satisfy min_items
        msg.Scores.AddRange(new[] { 1, 2, 2 }); // duplicate

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "scores" &&
            v.ConstraintId == "repeated.unique");
    }

    // --- items (element-level rules) ---

    [Fact]
    public void Items_Valid()
    {
        var msg = new RepeatedItemsTestMessage();
        msg.Values.AddRange(new[] { "abc", "defg" }); // all >= 3 chars

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Items_Invalid()
    {
        var msg = new RepeatedItemsTestMessage();
        msg.Values.AddRange(new[] { "ab", "defg" }); // "ab" is < 3 chars

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "values[0]" &&
            v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void Items_MultipleInvalid()
    {
        var msg = new RepeatedItemsTestMessage();
        msg.Values.AddRange(new[] { "ab", "c" }); // both < 3 chars

        var result = _validator.Validate(msg);

        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.FieldPath == "values[0]");
        Assert.Contains(result.Violations, v => v.FieldPath == "values[1]");
    }

    // --- map min_pairs ---

    [Fact]
    public void Map_MinPairs_Valid()
    {
        var msg = new MapTestMessage();
        msg.Labels.Add("key1", "value1");

        var result = _validator.Validate(msg);

        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "map.min_pairs");
    }

    [Fact]
    public void Map_MinPairs_Invalid()
    {
        var msg = new MapTestMessage();
        // labels is empty, min_pairs=1

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "labels" &&
            v.ConstraintId == "map.min_pairs");
    }

    // --- map keys/values rules ---

    [Fact]
    public void Map_Keys_Valid()
    {
        var msg = new MapKeysValuesTestMessage();
        msg.Entries.Add("ab", "abc"); // key >= 2, value >= 3

        var result = _validator.Validate(msg);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Map_Keys_Invalid()
    {
        var msg = new MapKeysValuesTestMessage();
        msg.Entries.Add("a", "abc"); // key "a" < 2 chars

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "entries[a].key" &&
            v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void Map_Values_Invalid()
    {
        var msg = new MapKeysValuesTestMessage();
        msg.Entries.Add("ab", "xy"); // value "xy" < 3 chars

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v =>
            v.FieldPath == "entries[ab]" &&
            v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void Map_KeysAndValues_BothInvalid()
    {
        var msg = new MapKeysValuesTestMessage();
        msg.Entries.Add("a", "xy"); // key < 2, value < 3

        var result = _validator.Validate(msg);

        Assert.Contains(result.Violations, v => v.FieldPath == "entries[a].key");
        Assert.Contains(result.Violations, v => v.FieldPath == "entries[a]");
    }
}
