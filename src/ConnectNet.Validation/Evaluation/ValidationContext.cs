using System;
using System.Collections.Generic;
using System.Threading;
using Buf.Validate;
using ConnectNet.Validation.Cel.Runtime;
using ConnectNet.Validation.Internal;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Evaluation;

/// <summary>
/// The state of one validation call: the violations collected so far, the evaluation budget
/// and cancellation shared by every rule, the <c>now</c> captured at the start, and the
/// structured field and rule paths of the value currently being evaluated.
/// </summary>
internal sealed class ValidationContext
{
    private readonly List<FieldPathElement> _fieldPath = new();
    private readonly List<FieldPathElement> _rulePrefix = new();

    public ValidationContext(ViolationCollector violations, EvalBudget budget, TimestampValue now)
    {
        Violations = violations;
        Budget = budget;
        Now = now;
    }

    public ViolationCollector Violations { get; }

    public EvalBudget Budget { get; }

    public TimestampValue Now { get; }

    public CancellationToken CancellationToken => Budget.CancellationToken;

    /// <summary>Message nesting depth of the value being evaluated.</summary>
    public int Depth { get; set; }

    /// <summary>True while the rules of a map key are evaluated: their violations are marked <c>for_key</c>.</summary>
    public bool ForKey { get; set; }

    /// <summary>True once the violation limit has been reached; callers stop traversing.</summary>
    public bool LimitReached => Violations.LimitReached();

    // --- field path ---

    public int FieldPathDepth => _fieldPath.Count;

    public void PushField(FieldPathElement element) => _fieldPath.Add(element);

    public void PopField() => _fieldPath.RemoveAt(_fieldPath.Count - 1);

    /// <summary>Swaps the innermost element for one carrying a subscript; returns the element to restore.</summary>
    public FieldPathElement ReplaceField(FieldPathElement element)
    {
        var previous = _fieldPath[_fieldPath.Count - 1];
        _fieldPath[_fieldPath.Count - 1] = element;
        return previous;
    }

    // --- rule path prefix (repeated.items, map.keys, map.values) ---

    public int RulePrefixCount => _rulePrefix.Count;

    public void PushRulePrefix(IReadOnlyList<FieldPathElement> elements) => _rulePrefix.AddRange(elements);

    public void TruncateRulePrefix(int count) => _rulePrefix.RemoveRange(count, _rulePrefix.Count - count);

    /// <summary>Clears the rule prefix while an embedded message is evaluated; returns the elements to restore.</summary>
    public FieldPathElement[] SuspendRulePrefix()
    {
        var saved = _rulePrefix.ToArray();
        _rulePrefix.Clear();
        return saved;
    }

    public void ResumeRulePrefix(FieldPathElement[] saved)
    {
        _rulePrefix.Clear();
        _rulePrefix.AddRange(saved);
    }

    // --- violations ---

    /// <summary>
    /// Records a violation at the current field path. <paramref name="ruleSuffix"/> is the rule
    /// path below the current prefix; null means the violation has no rule path (message-level
    /// rules, oneofs).
    /// </summary>
    public void AddViolation(string ruleId, string message, IReadOnlyList<FieldPathElement>? ruleSuffix,
        object? fieldValue, object? ruleValue, FieldDescriptor? field, FieldDescriptor? ruleField,
        IReadOnlyList<FieldPathElement>? fieldOverride = null)
    {
        var proto = new Buf.Validate.Violation
        {
            RuleId = ruleId,
            Message = Violation.Truncate(message, Violation.MaxMessageLength),
        };
        if (ForKey)
            proto.ForKey = true;

        var fieldElements = fieldOverride ?? _fieldPath;
        if (fieldElements.Count > 0)
        {
            var path = new FieldPath();
            foreach (var element in fieldElements)
                path.Elements.Add(element.Clone());
            proto.Field = path;
        }

        if (ruleSuffix != null && (_rulePrefix.Count > 0 || ruleSuffix.Count > 0))
        {
            var rule = new FieldPath();
            foreach (var element in _rulePrefix)
                rule.Elements.Add(element.Clone());
            foreach (var element in ruleSuffix)
                rule.Elements.Add(element.Clone());
            proto.Rule = rule;
        }

        Violations.Add(new Violation(proto, fieldValue, ruleValue, field, ruleField));
    }

    /// <summary>Charges traversal work against the shared budget.</summary>
    public void Consume(long cost) => Budget.Consume(cost);
}
