using System;
using System.Threading;
using ConnectNet.Validation.Cel.Runtime;
using ConnectNet.Validation.Evaluation;
using ConnectNet.Validation.Internal;
using Google.Protobuf;

namespace ConnectNet.Validation;

/// <summary>
/// Validates messages against their <c>buf.validate</c> rules: the standard rules of
/// <c>validate.proto</c>, custom <c>cel</c> expressions and predefined rule extensions, all
/// evaluated by the built-in CEL engine. Message types are compiled into plans on first use
/// and the plans are shared by every validation.
/// </summary>
public class ProtoValidator
{
    /// <summary>
    /// Hard cap on message nesting depth during validation. A recursive proto crafted to
    /// exceed this depth would cause StackOverflowException (which is uncatchable in .NET);
    /// rejecting early surfaces it as a normal validation result instead.
    /// </summary>
    public const int MaxRecursionDepth = 32;

    /// <summary>
    /// Default cap on how many violations one message may produce. The count is otherwise
    /// driven by the message itself — one violation per failing repeated element or map entry —
    /// so a single request within the receive limit could pin (and, through the error detail,
    /// reflect back) hundreds of megabytes. Reporting stops at this many violations and the
    /// result is marked <see cref="ValidationResult.Truncated"/>.
    /// </summary>
    public const int DefaultMaxViolations = 100;

    /// <summary>
    /// Default evaluation budget of one validation call, in evaluator work units: CEL
    /// operations and comprehension iterations, produced collection, string and byte sizes,
    /// and the fields, items and map entries traversed. Exhausting it throws
    /// <see cref="ValidationEvaluationException"/>; a message is never partially validated.
    /// </summary>
    public const long DefaultEvaluationBudget = 10_000_000;

    private readonly PlanBuilder _plans = new();
    private readonly int _maxViolations;
    private readonly long _budget;

    public ProtoValidator() : this(ignoreUnsupportedRules: false)
    {
    }

    /// <param name="ignoreUnsupportedRules">
    /// Retained for compatibility. Every rule of the pinned <c>validate.proto</c> is now
    /// evaluated, so there is no rule to skip; malformed rules, mismatched rule types and
    /// expressions that fail to compile throw <see cref="ValidationCompilationException"/>
    /// regardless of this setting.
    /// </param>
    public ProtoValidator(bool ignoreUnsupportedRules)
        : this(ignoreUnsupportedRules, DefaultMaxViolations)
    {
    }

    /// <param name="ignoreUnsupportedRules">See <see cref="ProtoValidator(bool)"/>.</param>
    /// <param name="maxViolations">
    /// Cap on the violations one message may produce; see <see cref="DefaultMaxViolations"/>.
    /// </param>
    public ProtoValidator(bool ignoreUnsupportedRules, int maxViolations)
        : this(ignoreUnsupportedRules, maxViolations, DefaultEvaluationBudget)
    {
    }

    internal ProtoValidator(bool ignoreUnsupportedRules, int maxViolations, long evaluationBudget)
    {
        if (maxViolations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxViolations), "maxViolations must be positive");
        if (evaluationBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(evaluationBudget), "evaluationBudget must be positive");

        IgnoreUnsupportedRules = ignoreUnsupportedRules;
        _maxViolations = maxViolations;
        _budget = evaluationBudget;
    }

    /// <summary>
    /// Retained for compatibility; see <see cref="ProtoValidator(bool)"/>.
    /// </summary>
    public bool IgnoreUnsupportedRules { get; }

    /// <summary>
    /// Validates a message against its rules.
    /// </summary>
    /// <exception cref="ValidationCompilationException">The message's schema has rules that cannot be compiled.</exception>
    /// <exception cref="ValidationEvaluationException">A rule failed at runtime, the evaluation budget was exhausted, or validation was cancelled.</exception>
    public ValidationResult Validate(IMessage message) => Validate(message, CancellationToken.None);

    /// <inheritdoc cref="Validate(IMessage)"/>
    public ValidationResult Validate(IMessage message, CancellationToken cancellationToken)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var plan = _plans.GetPlan(message.Descriptor);
        var violations = new ViolationCollector(_maxViolations);
        var context = new ValidationContext(violations, new EvalBudget(_budget, cancellationToken), Now());
        try
        {
            plan.EvaluateMessage(message, context);
        }
        catch (CelEvaluationException e)
        {
            // Budget exhaustion or cancellation raised while traversing, outside any rule.
            throw new ValidationEvaluationException(e.Message, e);
        }

        return violations.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Fail(violations.Violations, violations.Truncated);
    }

    private static TimestampValue Now()
    {
        var now = DateTime.UtcNow;
        var ticks = now.Ticks - DateTime.UnixEpoch.Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var nanos = (int)(ticks % TimeSpan.TicksPerSecond) * 100;
        return new TimestampValue(seconds, nanos);
    }
}
