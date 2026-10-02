using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// The CEL-backed rule evaluation: custom expressions on fields, items, map keys and values
/// and messages; predefined rule extensions; structured field and rule paths; compilation
/// versus evaluation failures; and the validation-wide budget and cancellation.
/// </summary>
public class RuleEvaluationTests
{
    private readonly ProtoValidator _validator = new();

    private static CelFieldTestMessage ValidCel() => new()
    {
        A = 1,
        B = "b",
        When = Timestamp.FromDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        Pattern = "abc",
    };

    // --- custom field rules ---

    [Fact]
    public void FieldRule_BoolResult_UsesTheRuleMessage()
    {
        var msg = ValidCel();
        msg.A = 0;

        var result = _validator.Validate(msg);

        var v = Assert.Single(result.Violations);
        Assert.Equal("a", v.FieldPath);
        Assert.Equal("a.positive", v.ConstraintId);
        Assert.Equal("a must be positive", v.Message);
        Assert.Equal(0, v.Value);
        Assert.Null(v.RuleValue);

        var field = Assert.Single(v.Field!.Elements);
        Assert.Equal(1, field.FieldNumber);
        Assert.Equal("a", field.FieldName);
        Assert.Equal(FieldDescriptorProto.Types.Type.Int32, field.FieldType);

        var rule = Assert.Single(v.Rule!.Elements);
        Assert.Equal("cel", rule.FieldName);
        Assert.Equal(0UL, rule.Index);
    }

    [Fact]
    public void FieldRule_StringResult_IsTheMessage()
    {
        var msg = ValidCel();
        msg.B = "";

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("b.nonempty", v.ConstraintId);
        Assert.Equal("b must not be empty", v.Message);
    }

    [Fact]
    public void ShorthandExpression_GetsTheDefaultMessage()
    {
        var msg = ValidCel();
        msg.Shorthand = 13;

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("this != 13", v.ConstraintId);
        Assert.Equal("\"this != 13\" returned false", v.Message);
        var rule = Assert.Single(v.Rule!.Elements);
        Assert.Equal("cel_expression", rule.FieldName);
    }

    [Fact]
    public void Now_IsBoundForTheWholeValidation()
    {
        var msg = ValidCel();
        msg.When = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1));

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("when.past", v.ConstraintId);
    }

    [Fact]
    public void Matches_CountsCodePoints()
    {
        var msg = ValidCel();
        msg.Pattern = "a\U0001F600b"; // three characters to RE2, four UTF-16 units to .NET

        Assert.True(_validator.Validate(msg).IsValid);

        msg.Pattern = "\U0001F600\U0001F600";
        Assert.Contains(_validator.Validate(msg).Violations, v => v.ConstraintId == "pattern.emoji");
    }

    // --- items, keys and values ---

    [Fact]
    public void ItemRule_ReportsTheIndexAndTheNestedRulePath()
    {
        var msg = ValidCel();
        msg.Items.AddRange(new[] { 2, 3 });

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("items[1]", v.FieldPath);
        Assert.Equal("item.even", v.ConstraintId);
        Assert.Equal(3, v.Value);
        var element = Assert.Single(v.Field!.Elements);
        Assert.Equal(1UL, element.Index);
        Assert.Equal(new[] { "repeated", "items", "cel" }, v.Rule!.Elements.Select(e => e.FieldName));
    }

    [Fact]
    public void MapKeyRule_IsMarkedForKey()
    {
        var msg = ValidCel();
        msg.Counts["Bad"] = 1;

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("counts[\"Bad\"]", v.FieldPath);
        Assert.True(v.ForKey);
        Assert.Equal("key.lower", v.ConstraintId);
        var element = Assert.Single(v.Field!.Elements);
        Assert.Equal("Bad", element.StringKey);
        Assert.Equal(FieldDescriptorProto.Types.Type.String, element.KeyType);
        Assert.Equal(FieldDescriptorProto.Types.Type.Int32, element.ValueType);
        Assert.Equal(new[] { "map", "keys", "cel" }, v.Rule!.Elements.Select(e => e.FieldName));
    }

    [Fact]
    public void MapValueRule_IsNotMarkedForKey()
    {
        var msg = ValidCel();
        msg.Counts["ok"] = -1;

        var v = Assert.Single(_validator.Validate(msg).Violations);

        Assert.Equal("counts[\"ok\"]", v.FieldPath);
        Assert.False(v.ForKey);
        Assert.Equal("value.nonneg", v.ConstraintId);
        Assert.Equal(new[] { "map", "values", "cel" }, v.Rule!.Elements.Select(e => e.FieldName));
    }

    [Fact]
    public void NestedMessages_PrefixTheFieldPath_AndResetTheRulePath()
    {
        var inner = ValidCel();
        inner.A = 0;
        var msg = new NestedCelTestMessage { Inner = inner };
        msg.ByName["x"] = ValidCel();
        msg.ByName["x"].Items.Add(1);

        var result = _validator.Validate(msg);

        Assert.Equal(2, result.Violations.Count);
        var a = Assert.Single(result.Violations, v => v.ConstraintId == "a.positive");
        Assert.Equal("inner.a", a.FieldPath);
        Assert.Equal(new[] { "cel" }, a.Rule!.Elements.Select(e => e.FieldName));

        var item = Assert.Single(result.Violations, v => v.ConstraintId == "item.even");
        Assert.Equal("by_name[\"x\"].items[0]", item.FieldPath);
        // The rule path is relative to the nested message's own field rules.
        Assert.Equal(new[] { "repeated", "items", "cel" }, item.Rule!.Elements.Select(e => e.FieldName));
        Assert.False(item.ForKey);
    }

    // --- message rules ---

    [Fact]
    public void MessageRule_HasNoFieldAndNoRulePath()
    {
        var result = _validator.Validate(new CelMessageTestMessage { Min = 5, Max = 1 });

        var v = Assert.Single(result.Violations);
        Assert.Equal("", v.FieldPath);
        Assert.Null(v.Field);
        Assert.Null(v.Rule);
        Assert.Equal("range", v.ConstraintId);
        Assert.Equal("min must not exceed max", v.Message);
    }

    [Fact]
    public void MessageShorthand_IsEvaluatedFirst()
    {
        var result = _validator.Validate(new CelMessageTestMessage { Min = -1, Max = 1 });

        var v = Assert.Single(result.Violations);
        Assert.Equal("this.min >= 0", v.ConstraintId);
    }

    [Fact]
    public void MessageOneof_RequiresExactlyOne()
    {
        var none = Assert.Single(_validator.Validate(new MessageOneofTestMessage()).Violations);
        Assert.Equal("message.oneof", none.ConstraintId);
        Assert.Equal("one of a, b must be set", none.Message);

        var both = Assert.Single(_validator.Validate(new MessageOneofTestMessage { A = "ab", B = "b" }).Violations);
        Assert.Equal("only one of a, b can be set", both.Message);

        Assert.True(_validator.Validate(new MessageOneofTestMessage { B = "b" }).IsValid);
    }

    [Fact]
    public void MessageOneofMembers_IgnoreTheirZeroValue()
    {
        // A member of a message oneof is implicitly ignore-if-zero-value, so a's min_len does
        // not fire when a is unset; it does once a is set.
        Assert.True(_validator.Validate(new MessageOneofTestMessage { B = "b" }).IsValid);

        var v = Assert.Single(_validator.Validate(new MessageOneofTestMessage { A = "a" }).Violations);
        Assert.Equal("string.min_len", v.ConstraintId);
    }

    // --- recursive schemas ---

    [Fact]
    public void RecursiveSchema_SharesOnePlan()
    {
        var msg = new RecursiveTestMessage
        {
            Name = "root",
            Child = new RecursiveTestMessage { Name = "c", Child = new RecursiveTestMessage() },
        };
        msg.Children.Add(new RecursiveTestMessage { Name = "ok" });
        msg.Children.Add(new RecursiveTestMessage());

        var result = _validator.Validate(msg);

        Assert.Equal(new[] { "child.child.name", "children[1].name" }, result.Violations.Select(v => v.FieldPath).OrderBy(p => p));
    }

    // --- predefined rules ---

    [Fact]
    public void PredefinedRule_IsEvaluatedWithItsRuleValue()
    {
        var result = _validator.Validate(new PredefinedRulesTestMessage { Title = "hello" });

        var v = Assert.Single(result.Violations);
        Assert.Equal("string.starts_upper", v.ConstraintId);
        Assert.Equal("must start with an upper-case letter", v.Message);
        Assert.Equal(true, v.RuleValue);
        Assert.Equal(new[] { "string", "[validation_test.starts_upper]" }, v.Rule!.Elements.Select(e => e.FieldName));
        Assert.Equal(1001, v.Rule.Elements[1].FieldNumber);
    }

    [Fact]
    public void PredefinedRule_BindsRuleForRepeatedValues()
    {
        Assert.True(_validator.Validate(new PredefinedRulesTestMessage { Path = "/api/users" }).IsValid);

        var v = Assert.Single(_validator.Validate(new PredefinedRulesTestMessage { Path = "/other" }).Violations);
        Assert.Equal("string.one_of_prefixes", v.ConstraintId);
        // Lists render as the pinned cel-spec expects (`%s` of a list of strings is unquoted).
        Assert.Equal("must start with one of [/api, /v1]", v.Message);
    }

    // --- compilation versus evaluation failures ---

    [Fact]
    public void MismatchedRuleType_IsACompilationError()
    {
        var ex = Assert.Throws<ValidationCompilationException>(() => _validator.Validate(new MismatchedRuleTestMessage { Val = "x" }));

        Assert.Contains("expected rule", ex.Message);
        Assert.Contains("buf.validate.FieldRules.string", ex.Message);
        Assert.Contains("buf.validate.FieldRules.int32", ex.Message);
    }

    [Fact]
    public void UnparsableExpression_IsACompilationError()
    {
        var ex = Assert.Throws<ValidationCompilationException>(() => _validator.Validate(new BadExpressionTestMessage()));

        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void NonBoolNonStringExpression_IsACompilationError()
    {
        var ex = Assert.Throws<ValidationCompilationException>(() => _validator.Validate(new NonBoolExpressionTestMessage()));

        Assert.Contains("wanted either bool or string", ex.Message);
    }

    [Fact]
    public void CompilationError_IsRaisedEvenWhenTheFieldIsAbsent()
    {
        // The schema is checked, not the message: an unset field with broken rules still fails.
        Assert.Throws<ValidationCompilationException>(() => _validator.Validate(new MismatchedRuleTestMessage()));
    }

    [Fact]
    public void RuntimeTypeError_IsAnEvaluationError()
    {
        var ex = Assert.Throws<ValidationEvaluationException>(() => _validator.Validate(new DynRuntimeErrorTestMessage { A = 1 }));

        Assert.Contains("dyn", ex.Message);
    }

    // --- limits ---

    [Fact]
    public void Cancellation_StopsEvaluation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var msg = new ExpensiveTestMessage();
        msg.Values.Add(1);

        var ex = Assert.Throws<ValidationEvaluationException>(() => _validator.Validate(msg, cts.Token));

        Assert.IsType<OperationCanceledException>(ex.InnerException?.InnerException);
    }

    [Fact]
    public void Budget_IsSharedByTheWholeValidation()
    {
        var msg = new ExpensiveTestMessage();
        msg.Values.AddRange(Enumerable.Range(1, 200)); // 40,000 inner iterations

        Assert.True(_validator.Validate(msg).IsValid);

        var small = new ProtoValidator(ignoreUnsupportedRules: false, maxViolations: 100, evaluationBudget: 10_000);
        var ex = Assert.Throws<ValidationEvaluationException>(() => small.Validate(msg));
        Assert.Contains("budget", ex.Message);
    }

    [Fact]
    public void ConcurrentValidations_ShareThePlan()
    {
        var results = new ValidationResult[64];
        Parallel.For(0, results.Length, i =>
        {
            var msg = ValidCel();
            if (i % 2 == 1)
                msg.A = 0;
            results[i] = _validator.Validate(msg);
        });

        for (int i = 0; i < results.Length; i++)
            Assert.Equal(i % 2 == 0, results[i].IsValid);
    }

    [Fact]
    public void ToProto_CarriesTheStructuredPaths()
    {
        var msg = ValidCel();
        msg.Counts["Bad"] = 1;

        var proto = Assert.Single(_validator.Validate(msg).Violations).ToProto();

        Assert.Equal("key.lower", proto.RuleId);
        Assert.True(proto.ForKey);
        Assert.Equal("counts", proto.Field.Elements[0].FieldName);
        Assert.Equal("Bad", proto.Field.Elements[0].StringKey);
        Assert.Equal(new[] { "map", "keys", "cel" }, proto.Rule.Elements.Select(e => e.FieldName));
    }
}
