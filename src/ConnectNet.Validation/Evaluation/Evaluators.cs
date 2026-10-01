using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Buf.Validate;
using ConnectNet.Validation.Cel.Protobuf;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Evaluation;

/// <summary>Applies rules to one value: a field, a repeated item, or a map key or value.</summary>
internal interface IValueEvaluator
{
    /// <summary>True when the evaluator can never produce a violation and may be dropped.</summary>
    bool IsTautology { get; }

    void Evaluate(object? value, ValidationContext ctx);
}

/// <summary>Applies rules to a message.</summary>
internal interface IMessageEvaluator
{
    bool IsTautology { get; }

    void EvaluateMessage(IMessage message, ValidationContext ctx);
}

/// <summary>
/// The rules of one value (the reference implementation's <c>value</c>): the rules applied to
/// the value itself, the rules applied to the messages nested under it, and, for repeated
/// items and map entries, the <c>ignore</c> zero-value check and the rule-path prefix
/// (<c>repeated.items</c>, <c>map.keys</c>, <c>map.values</c>).
/// </summary>
internal sealed class ValueRules
{
    public FieldDescriptor? Descriptor { get; init; }

    public List<IValueEvaluator> Rules { get; } = new();

    public List<IValueEvaluator> Nested { get; } = new();

    public IReadOnlyList<FieldPathElement> NestedRulePrefix { get; init; } = Array.Empty<FieldPathElement>();

    public bool IgnoreEmpty { get; set; }

    public Func<object?, bool>? IsZero { get; set; }

    public bool IsTautology => Rules.Count == 0 && Nested.Count == 0;

    public void Append(IValueEvaluator evaluator)
    {
        if (!evaluator.IsTautology)
            Rules.Add(evaluator);
    }

    public void AppendNested(IValueEvaluator evaluator)
    {
        if (!evaluator.IsTautology)
            Nested.Add(evaluator);
    }

    public void Evaluate(object? value, ValidationContext ctx)
    {
        if (IgnoreEmpty && IsZero!(value))
            return;
        var prefixCount = ctx.RulePrefixCount;
        if (NestedRulePrefix.Count > 0)
            ctx.PushRulePrefix(NestedRulePrefix);
        try
        {
            foreach (var rule in Rules)
            {
                if (ctx.LimitReached) return;
                rule.Evaluate(value, ctx);
            }
            foreach (var nested in Nested)
            {
                if (ctx.LimitReached) return;
                nested.Evaluate(value, ctx);
            }
        }
        finally
        {
            ctx.TruncateRulePrefix(prefixCount);
        }
    }
}

/// <summary>A field of a message: presence, <c>required</c>, <c>ignore</c>, then the value's rules.</summary>
internal sealed class FieldEvaluator : IMessageEvaluator
{
    private static readonly FieldPathElement[] RequiredRulePath =
    {
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.RequiredFieldNumber)),
    };

    private static readonly FieldDescriptor RequiredRuleField =
        FieldRules.Descriptor.FindFieldByNumber(FieldRules.RequiredFieldNumber);

    public FieldEvaluator(FieldDescriptor field)
    {
        Field = field;
        Element = FieldPathElements.ForField(field);
        Value = new ValueRules { Descriptor = field };
    }

    public FieldDescriptor Field { get; }

    public FieldPathElement Element { get; }

    public ValueRules Value { get; }

    public bool Required { get; set; }

    public Ignore Ignore { get; set; }

    /// <summary>A compilation failure of this field's rules, surfaced when the field is evaluated.</summary>
    public Exception? Error { get; set; }

    public bool IsTautology => !Required && Value.IsTautology && Error == null;

    public void EvaluateMessage(IMessage message, ValidationContext ctx)
    {
        if (Ignore == Ignore.Always)
            return;
        if (Error != null)
            throw Error;

        ctx.Consume(1);
        var isSet = ProtoValues.IsSet(message, Field);
        if (Required && !isSet)
        {
            // An absent required field reports only that; its other rules are not applied.
            ctx.PushField(Element);
            ctx.AddViolation("required", "value is required", RequiredRulePath, null, true, Field, RequiredRuleField);
            ctx.PopField();
            return;
        }
        if ((Field.HasPresence || Ignore == Ignore.IfZeroValue) && !isSet)
            return;

        ctx.PushField(Element);
        try
        {
            Value.Evaluate(Field.Accessor.GetValue(message), ctx);
        }
        finally
        {
            ctx.PopField();
        }
    }
}

/// <summary>The compiled plan of a message type.</summary>
internal sealed class MessagePlan : IMessageEvaluator
{
    public MessageDescriptor Descriptor { get; }

    public MessagePlan(MessageDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>A compilation failure of the message's own rules, surfaced when it is evaluated.</summary>
    public Exception? Error { get; set; }

    public List<IMessageEvaluator> Evaluators { get; } = new();

    public List<IMessageEvaluator> Nested { get; } = new();

    // Never a tautology: a recursive schema would otherwise be judged before it is built.
    public bool IsTautology => false;

    public void Append(IMessageEvaluator evaluator)
    {
        if (!evaluator.IsTautology)
            Evaluators.Add(evaluator);
    }

    public void AppendNested(IMessageEvaluator evaluator)
    {
        if (!evaluator.IsTautology)
            Nested.Add(evaluator);
    }

    public void EvaluateMessage(IMessage message, ValidationContext ctx)
    {
        if (Error != null)
            throw Error;
        foreach (var evaluator in Evaluators)
        {
            if (ctx.LimitReached) return;
            evaluator.EvaluateMessage(message, ctx);
        }
        foreach (var nested in Nested)
        {
            if (ctx.LimitReached) return;
            nested.EvaluateMessage(message, ctx);
        }
    }
}

/// <summary>A message nested under a field, repeated item or map value.</summary>
internal sealed class EmbeddedMessage : IValueEvaluator
{
    public EmbeddedMessage(MessagePlan plan)
    {
        Plan = plan;
    }

    public MessagePlan Plan { get; }

    public bool IsTautology => Plan.IsTautology;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        if (value is not IMessage message)
            return;
        if (ctx.Depth + 1 >= ProtoValidator.MaxRecursionDepth)
        {
            ctx.AddViolation("recursion_limit",
                $"message nesting exceeds validation recursion limit ({ProtoValidator.MaxRecursionDepth})",
                null, value, null, null, null);
            return;
        }
        var saved = ctx.SuspendRulePrefix();
        ctx.Depth++;
        try
        {
            Plan.EvaluateMessage(message, ctx);
        }
        finally
        {
            ctx.Depth--;
            ctx.ResumeRulePrefix(saved);
        }
    }
}

/// <summary>The items of a repeated field.</summary>
internal sealed class ListItems : IValueEvaluator
{
    public ListItems(FieldDescriptor field, FieldPathElement element, ValueRules items)
    {
        Field = field;
        Element = element;
        Items = items;
    }

    public FieldDescriptor Field { get; }

    public FieldPathElement Element { get; }

    public ValueRules Items { get; }

    public bool IsTautology => Items.IsTautology;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        if (value is not IList list)
            return;
        for (int i = 0; i < list.Count; i++)
        {
            if (ctx.LimitReached) return;
            ctx.Consume(1);
            var restore = ctx.ReplaceField(FieldPathElements.ForItem(Element, i));
            try
            {
                Items.Evaluate(list[i], ctx);
            }
            finally
            {
                ctx.ReplaceField(restore);
            }
        }
    }
}

/// <summary>The keys and values of a map field.</summary>
internal sealed class MapPairs : IValueEvaluator
{
    public MapPairs(FieldDescriptor field, FieldPathElement element, ValueRules keys, ValueRules values)
    {
        Field = field;
        Element = element;
        Keys = keys;
        Values = values;
    }

    public FieldDescriptor Field { get; }

    public FieldPathElement Element { get; }

    public ValueRules Keys { get; }

    public ValueRules Values { get; }

    public bool IsTautology => Keys.IsTautology && Values.IsTautology;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        if (value is not IDictionary dictionary)
            return;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (ctx.LimitReached) return;
            ctx.Consume(1);
            var restore = ctx.ReplaceField(FieldPathElements.ForEntry(Element, Field, entry.Key));
            try
            {
                ctx.ForKey = true;
                Keys.Evaluate(entry.Key, ctx);
                ctx.ForKey = false;
                if (ctx.LimitReached) return;
                Values.Evaluate(entry.Value, ctx);
            }
            finally
            {
                ctx.ForKey = false;
                ctx.ReplaceField(restore);
            }
        }
    }
}

/// <summary><c>enum.defined_only</c>.</summary>
internal sealed class DefinedEnum : IValueEvaluator
{
    private static readonly FieldDescriptor RuleField = EnumRules.Descriptor.FindFieldByNumber(EnumRules.DefinedOnlyFieldNumber);

    private static readonly FieldPathElement[] RulePath =
    {
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.EnumFieldNumber)),
        FieldPathElements.ForField(RuleField),
    };

    public DefinedEnum(FieldDescriptor field)
    {
        Field = field;
    }

    public FieldDescriptor Field { get; }

    public bool IsTautology => false;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        var number = value switch
        {
            int i => i,
            Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture),
            _ => 0,
        };
        if (Field.EnumType.FindValueByNumber(number) == null)
        {
            ctx.AddViolation("enum.defined_only", "value must be one of the defined enum values", RulePath,
                value, true, Field, RuleField);
        }
    }
}

/// <summary><c>any.in</c> and <c>any.not_in</c> on a <c>google.protobuf.Any</c>'s type URL.</summary>
internal sealed class AnyTypeUrl : IValueEvaluator
{
    private static readonly FieldPathElement AnyElement =
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.AnyFieldNumber));

    private static readonly FieldDescriptor InField = AnyRules.Descriptor.FindFieldByNumber(AnyRules.InFieldNumber);
    private static readonly FieldDescriptor NotInField = AnyRules.Descriptor.FindFieldByNumber(AnyRules.NotInFieldNumber);
    private static readonly FieldPathElement[] InPath = { AnyElement, FieldPathElements.ForField(InField) };
    private static readonly FieldPathElement[] NotInPath = { AnyElement, FieldPathElements.ForField(NotInField) };

    private readonly HashSet<string> _in;
    private readonly HashSet<string> _notIn;
    private readonly object _inValue;
    private readonly object _notInValue;

    public AnyTypeUrl(FieldDescriptor field, AnyRules rules)
    {
        Field = field;
        _in = new HashSet<string>(rules.In, StringComparer.Ordinal);
        _notIn = new HashSet<string>(rules.NotIn, StringComparer.Ordinal);
        _inValue = rules.In;
        _notInValue = rules.NotIn;
    }

    public FieldDescriptor Field { get; }

    public bool IsTautology => _in.Count == 0 && _notIn.Count == 0;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        if (value is not Google.Protobuf.WellKnownTypes.Any any)
            return;
        if (_in.Count > 0 && !_in.Contains(any.TypeUrl))
        {
            ctx.AddViolation("any.in", "type URL must be in the allow list", InPath, value, _inValue, Field, InField);
        }
        if (_notIn.Count > 0 && _notIn.Contains(any.TypeUrl))
        {
            ctx.AddViolation("any.not_in", "type URL must not be in the block list", NotInPath, value, _notInValue, Field, NotInField);
        }
    }
}

/// <summary>A proto <c>oneof</c> with <c>(buf.validate.oneof).required</c>.</summary>
internal sealed class RequiredOneof : IMessageEvaluator
{
    public RequiredOneof(OneofDescriptor oneof)
    {
        Oneof = oneof;
        Element = FieldPathElements.ForOneof(oneof);
    }

    public OneofDescriptor Oneof { get; }

    public FieldPathElement Element { get; }

    public bool IsTautology => false;

    public void EvaluateMessage(IMessage message, ValidationContext ctx)
    {
        if (Oneof.Accessor.GetCaseFieldDescriptor(message) != null)
            return;
        ctx.PushField(Element);
        ctx.AddViolation("required", "exactly one field is required in oneof", null, null, null, null, null);
        ctx.PopField();
    }
}

/// <summary>A <c>(buf.validate.message).oneof</c> rule over a set of fields.</summary>
internal sealed class MessageOneof : IMessageEvaluator
{
    public MessageOneof(IReadOnlyList<FieldDescriptor> fields, bool required)
    {
        Fields = fields;
        Required = required;
    }

    public IReadOnlyList<FieldDescriptor> Fields { get; }

    public bool Required { get; }

    public bool IsTautology => false;

    private string FormatFields() => string.Join(", ", Fields.Select(f => f.Name));

    public void EvaluateMessage(IMessage message, ValidationContext ctx)
    {
        if (Fields.Count == 0)
            return;
        var count = 0;
        foreach (var field in Fields)
        {
            if (ProtoValues.IsSet(message, field))
                count++;
        }
        if (count > 1)
        {
            ctx.AddViolation("message.oneof", $"only one of {FormatFields()} can be set", null, null, null, null, null);
            return;
        }
        if (Required && count != 1)
        {
            ctx.AddViolation("message.oneof", $"one of {FormatFields()} must be set", null, null, null, null, null);
        }
    }
}

/// <summary>One compiled CEL rule with the values bound to it at plan time.</summary>
internal sealed class CompiledRule
{
    public CompiledRule(CelProgram program, Rule source, IReadOnlyList<FieldPathElement> rulePath)
    {
        Program = program;
        Source = source;
        RulePath = rulePath;
    }

    public CelProgram Program { get; }

    public Rule Source { get; }

    /// <summary>The rule path below any <c>repeated.items</c> / <c>map.keys</c> / <c>map.values</c> prefix.</summary>
    public IReadOnlyList<FieldPathElement> RulePath { get; }

    /// <summary>The <c>rules</c> message (a standard rule's rule message), when there is one.</summary>
    public CelValue? RulesValue { get; init; }

    /// <summary>The <c>rule</c> value (a standard rule's own field value), when there is one.</summary>
    public CelValue? RuleValue { get; init; }

    public object? RuleRaw { get; init; }

    public FieldDescriptor? RuleField { get; init; }

    public CompiledRule WithRuleValues(CelValue rules, CelValue? rule, object? ruleRaw, FieldDescriptor ruleField) =>
        new(Program, Source, RulePath) { RulesValue = rules, RuleValue = rule, RuleRaw = ruleRaw, RuleField = ruleField };
}

/// <summary>The bindings of one rule evaluation.</summary>
internal sealed class RuleActivation : Activation
{
    private readonly CelValue _this;
    private readonly CelValue? _rules;
    private readonly CelValue? _rule;
    private readonly CelValue _now;

    public RuleActivation(CelValue thisValue, CelValue? rules, CelValue? rule, CelValue now)
    {
        _this = thisValue;
        _rules = rules;
        _rule = rule;
        _now = now;
    }

    public override bool TryResolve(string name, out CelValue value)
    {
        switch (name)
        {
            case "this": value = _this; return true;
            case "rules" when _rules != null: value = _rules; return true;
            case "rule" when _rule != null: value = _rule; return true;
            case "now": value = _now; return true;
            default: value = null!; return false;
        }
    }
}

/// <summary>Runs a set of compiled rules against one <c>this</c> value.</summary>
internal sealed class RuleProgramSet
{
    public RuleProgramSet(IReadOnlyList<CompiledRule> rules)
    {
        Rules = rules;
    }

    public IReadOnlyList<CompiledRule> Rules { get; }

    public void Evaluate(CelValue thisValue, object? rawValue, FieldDescriptor? field, ValidationContext ctx)
    {
        foreach (var rule in Rules)
        {
            if (ctx.LimitReached) return;
            ctx.Consume(1);
            var activation = new RuleActivation(thisValue, rule.RulesValue, rule.RuleValue, ctx.Now);
            CelValue result;
            try
            {
                result = rule.Program.Evaluate(activation, ctx.Budget);
            }
            catch (CelEvaluationException e)
            {
                throw new ValidationEvaluationException($"error evaluating {rule.Source.Id}: {e.Message}", e);
            }

            switch (result)
            {
                case ErrorValue error:
                    throw new ValidationEvaluationException($"error evaluating {rule.Source.Id}: {error.Message}");
                case StringValue s:
                    if (s.Value.Length > 0)
                        ctx.AddViolation(rule.Source.Id, s.Value, rule.RulePath, rawValue, rule.RuleRaw, field, rule.RuleField);
                    break;
                case BoolValue b:
                    if (!b.Value)
                    {
                        var message = rule.Source.Message;
                        if (string.IsNullOrEmpty(message))
                            message = FieldPathElements.GoQuote(rule.Source.Expression) + " returned false";
                        ctx.AddViolation(rule.Source.Id, message, rule.RulePath, rawValue, rule.RuleRaw, field, rule.RuleField);
                    }
                    break;
                default:
                    throw new ValidationEvaluationException(
                        $"error evaluating {rule.Source.Id}: resolved to an unexpected type {result.TypeName}");
            }
        }
    }
}

/// <summary>CEL rules (standard, custom or predefined) applied to a field value.</summary>
internal sealed class FieldCelRules : IValueEvaluator
{
    private readonly ProtoTypeProvider _provider;
    private readonly bool _forItems;

    public FieldCelRules(ProtoTypeProvider provider, FieldDescriptor field, bool forItems, IReadOnlyList<CompiledRule> rules)
    {
        _provider = provider;
        _forItems = forItems;
        Field = field;
        Programs = new RuleProgramSet(rules);
    }

    /// <summary>The field whose value <c>this</c> is: the element's descriptor for items, keys and values.</summary>
    public FieldDescriptor Field { get; }

    public RuleProgramSet Programs { get; }

    public bool IsTautology => Programs.Rules.Count == 0;

    public void Evaluate(object? value, ValidationContext ctx)
    {
        var thisValue = ProtoValues.ValueToCel(_provider, Field, value, _forItems);
        if (thisValue is ErrorValue error)
            throw new ValidationEvaluationException("error converting " + Field.FullName + ": " + error.Message);
        Programs.Evaluate(thisValue, value, Field, ctx);
    }
}

/// <summary>CEL rules applied to a message (<c>(buf.validate.message).cel</c>).</summary>
internal sealed class MessageCelRules : IMessageEvaluator
{
    private readonly ProtoTypeProvider _provider;

    public MessageCelRules(ProtoTypeProvider provider, IReadOnlyList<CompiledRule> rules)
    {
        _provider = provider;
        Programs = new RuleProgramSet(rules);
    }

    public RuleProgramSet Programs { get; }

    public bool IsTautology => Programs.Rules.Count == 0;

    public void EvaluateMessage(IMessage message, ValidationContext ctx)
    {
        Programs.Evaluate(new ProtoMessageValue(_provider, message), message, null, ctx);
    }
}
