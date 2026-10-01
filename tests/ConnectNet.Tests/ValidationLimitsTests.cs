using System;
using System.Linq;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// Every way a message or a schema could make validation do unbounded work is cut off by a
/// limit: violations, traversal, comprehensions, output construction, compilation size and
/// depth, and the size of the diagnostics a violation stores.
/// </summary>
public class ValidationLimitsTests
{
    private static ProtoValidator WithBudget(long budget) =>
        new(ignoreUnsupportedRules: false, maxViolations: ProtoValidator.DefaultMaxViolations, evaluationBudget: budget);

    [Fact]
    public void OutputConstruction_IsChargedToTheBudget()
    {
        var msg = new LimitsTestMessage();
        msg.Values.AddRange(Enumerable.Range(1, 4000)); // 16 million produced elements

        var ex = Assert.Throws<ValidationEvaluationException>(() => new ProtoValidator().Validate(msg));
        Assert.Contains("budget", ex.Message);

        msg.Values.Clear();
        msg.Values.AddRange(Enumerable.Range(1, 100));
        Assert.True(new ProtoValidator().Validate(msg).IsValid);
    }

    [Fact]
    public void Traversal_IsChargedToTheBudget()
    {
        // No rule fires and no violation is produced; the fields visited still count.
        var msg = new LimitsTestMessage();
        for (int i = 0; i < 5000; i++)
            msg.Children.Add(new LimitsTestMessage { N = i });

        Assert.True(new ProtoValidator().Validate(msg).IsValid);
        var ex = Assert.Throws<ValidationEvaluationException>(() => WithBudget(1000).Validate(msg));
        Assert.Contains("budget", ex.Message);
    }

    [Fact]
    public void Violations_StopAtTheLimit_BeforeTheBudget()
    {
        var msg = new LimitsTestMessage();
        for (int i = 0; i < 1000; i++)
            msg.Children.Add(new LimitsTestMessage { N = -1 });

        var result = new ProtoValidator().Validate(msg);

        Assert.Equal(ProtoValidator.DefaultMaxViolations, result.Violations.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void DiagnosticMessages_AreTruncated()
    {
        var msg = new LimitsTestMessage { Text = new string('x', 100_000) };

        var v = Assert.Single(new ProtoValidator().Validate(msg).Violations);

        Assert.StartsWith("bad value: xxx", v.Message);
        Assert.True(v.Message.Length <= Violation.MaxMessageLength + 3, v.Message.Length.ToString());
    }

    [Fact]
    public void Compilation_RejectsTooDeepAnExpression()
    {
        var ex = Assert.Throws<ValidationCompilationException>(() => new ProtoValidator().Validate(new DeepExpressionTestMessage { Val = 1 }));

        Assert.Contains("recursion limit", ex.Message);
    }

    [Fact]
    public void Compilation_RejectsTooLongAnExpression()
    {
        var ex = Assert.Throws<ValidationCompilationException>(() => new ProtoValidator().Validate(new LongExpressionTestMessage { Val = 1 }));

        Assert.Contains("size", ex.Message);
    }

    [Fact]
    public void RecursionDepth_IsBounded()
    {
        var root = new LimitsTestMessage { N = 0 };
        var current = root;
        for (int i = 0; i < ProtoValidator.MaxRecursionDepth + 5; i++)
        {
            var child = new LimitsTestMessage { N = 0 };
            current.Children.Add(child);
            current = child;
        }

        var result = new ProtoValidator().Validate(root);

        var v = Assert.Single(result.Violations);
        Assert.Equal("recursion_limit", v.ConstraintId);
    }

    [Fact]
    public void BudgetExhaustion_IsNeverAPartialResult()
    {
        var msg = new LimitsTestMessage();
        msg.Children.Add(new LimitsTestMessage { N = -1 }); // would be a violation
        msg.Values.AddRange(Enumerable.Range(1, 4000));     // but the budget runs out first

        Assert.Throws<ValidationEvaluationException>(() => new ProtoValidator().Validate(msg));
    }
}
