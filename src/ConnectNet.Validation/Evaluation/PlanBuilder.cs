using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Buf.Validate;
using ConnectNet.Validation.Cel;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Protobuf;
using ConnectNet.Validation.Cel.Protovalidate;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Evaluation;

/// <summary>
/// Compiles message descriptors into <see cref="MessagePlan"/>s, following the structure of
/// the reference implementation's builder: message expressions, message oneof rules, proto
/// oneofs, then each field's value pipeline (ignore, custom expressions, embedded message,
/// wrapper unwrapping, standard rules, Any, map and repeated). Plans are cached by descriptor
/// identity; recursive schemas resolve to the plan under construction.
/// </summary>
internal sealed class PlanBuilder
{
    private static readonly FieldDescriptor CelExpressionField = FieldRules.Descriptor.FindFieldByNumber(FieldRules.CelExpressionFieldNumber);
    private static readonly FieldDescriptor CelField = FieldRules.Descriptor.FindFieldByNumber(FieldRules.CelFieldNumber);
    private static readonly FieldPathElement CelExpressionElement = FieldPathElements.ForField(CelExpressionField);
    private static readonly FieldPathElement CelElement = FieldPathElements.ForField(CelField);

    private static readonly FieldPathElement[] ItemsPrefix =
    {
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.RepeatedFieldNumber)),
        FieldPathElements.ForField(RepeatedRules.Descriptor.FindFieldByNumber(RepeatedRules.ItemsFieldNumber)),
    };

    private static readonly FieldPathElement[] KeysPrefix =
    {
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.MapFieldNumber)),
        FieldPathElements.ForField(MapRules.Descriptor.FindFieldByNumber(MapRules.KeysFieldNumber)),
    };

    private static readonly FieldPathElement[] ValuesPrefix =
    {
        FieldPathElements.ForField(FieldRules.Descriptor.FindFieldByNumber(FieldRules.MapFieldNumber)),
        FieldPathElements.ForField(MapRules.Descriptor.FindFieldByNumber(MapRules.ValuesFieldNumber)),
    };

    private static readonly Dictionary<FieldType, int> ExpectedScalarRules = new()
    {
        [FieldType.Float] = FieldRules.FloatFieldNumber,
        [FieldType.Double] = FieldRules.DoubleFieldNumber,
        [FieldType.Int32] = FieldRules.Int32FieldNumber,
        [FieldType.Int64] = FieldRules.Int64FieldNumber,
        [FieldType.UInt32] = FieldRules.Uint32FieldNumber,
        [FieldType.UInt64] = FieldRules.Uint64FieldNumber,
        [FieldType.SInt32] = FieldRules.Sint32FieldNumber,
        [FieldType.SInt64] = FieldRules.Sint64FieldNumber,
        [FieldType.Fixed32] = FieldRules.Fixed32FieldNumber,
        [FieldType.Fixed64] = FieldRules.Fixed64FieldNumber,
        [FieldType.SFixed32] = FieldRules.Sfixed32FieldNumber,
        [FieldType.SFixed64] = FieldRules.Sfixed64FieldNumber,
        [FieldType.Bool] = FieldRules.BoolFieldNumber,
        [FieldType.String] = FieldRules.StringFieldNumber,
        [FieldType.Bytes] = FieldRules.BytesFieldNumber,
        [FieldType.Enum] = FieldRules.EnumFieldNumber,
    };

    private static readonly Dictionary<string, int> ExpectedWellKnownRules = new(StringComparer.Ordinal)
    {
        ["google.protobuf.Any"] = FieldRules.AnyFieldNumber,
        ["google.protobuf.Duration"] = FieldRules.DurationFieldNumber,
        ["google.protobuf.FieldMask"] = FieldRules.FieldMaskFieldNumber,
        ["google.protobuf.Timestamp"] = FieldRules.TimestampFieldNumber,
    };

    private static readonly Dictionary<string, int> ExpectedWrapperRules = new(StringComparer.Ordinal)
    {
        ["google.protobuf.BoolValue"] = FieldRules.BoolFieldNumber,
        ["google.protobuf.BytesValue"] = FieldRules.BytesFieldNumber,
        ["google.protobuf.DoubleValue"] = FieldRules.DoubleFieldNumber,
        ["google.protobuf.FloatValue"] = FieldRules.FloatFieldNumber,
        ["google.protobuf.Int32Value"] = FieldRules.Int32FieldNumber,
        ["google.protobuf.Int64Value"] = FieldRules.Int64FieldNumber,
        ["google.protobuf.StringValue"] = FieldRules.StringFieldNumber,
        ["google.protobuf.UInt32Value"] = FieldRules.Uint32FieldNumber,
        ["google.protobuf.UInt64Value"] = FieldRules.Uint64FieldNumber,
    };

    // Keyed by descriptor identity so that distinct descriptors sharing a full name never reuse
    // each other's plan, and so dynamically loaded schemas are not rooted for the process lifetime.
    private readonly ConditionalWeakTable<MessageDescriptor, MessagePlan> _plans = new();
    private readonly ConditionalWeakTable<FileDescriptor, ProtoTypeProvider> _providers = new();
    private readonly ConditionalWeakTable<FieldDescriptor, IReadOnlyList<CompiledRule>> _standardRules = new();
    private readonly object _lock = new();

    // Plans under construction, visible only to the build holding the lock: a recursive
    // schema finds its own plan here, while other threads never see a half-built one.
    private Dictionary<MessageDescriptor, MessagePlan>? _building;

    /// <summary>The plan of a message type, built on first use.</summary>
    public MessagePlan GetPlan(MessageDescriptor descriptor)
    {
        if (_plans.TryGetValue(descriptor, out var plan))
            return plan;
        lock (_lock)
        {
            if (_plans.TryGetValue(descriptor, out plan))
                return plan;
            _building = new Dictionary<MessageDescriptor, MessagePlan>(ReferenceEqualityComparer.Instance);
            try
            {
                plan = Build(descriptor);
                // Publish every plan this build completed, including the embedded ones.
                foreach (var (built, builtPlan) in _building)
                    _plans.AddOrUpdate(built, builtPlan);
                return plan;
            }
            finally
            {
                _building = null;
            }
        }
    }

    private MessagePlan Build(MessageDescriptor descriptor)
    {
        if (_plans.TryGetValue(descriptor, out var existing))
            return existing;
        if (_building!.TryGetValue(descriptor, out existing))
            return existing;
        var plan = new MessagePlan(descriptor);
        _building.Add(descriptor, plan);
        BuildMessage(descriptor, plan);
        return plan;
    }

    // ---- environments ----

    private ProtoTypeProvider ProviderFor(FileDescriptor file)
    {
        if (_providers.TryGetValue(file, out var provider))
            return provider;
        provider = new ProtoTypeProvider(new[]
        {
            file,
            ValidateReflection.Descriptor,
            AnyReflection.Descriptor,
            DurationReflection.Descriptor,
            EmptyReflection.Descriptor,
            FieldMaskReflection.Descriptor,
            StructReflection.Descriptor,
            TimestampReflection.Descriptor,
            WrappersReflection.Descriptor,
        });
        _providers.Add(file, provider);
        return provider;
    }

    private static CelEnvironment NewEnvironment(ProtoTypeProvider provider, CelType thisType)
    {
        var env = new CelEnvironment(null, provider, provider)
            .AddStandardLibrary()
            .AddStringsExtension();
        ProtovalidateFunctions.AddTo(env);
        env.AddVariable("this", thisType);
        return env;
    }

    /// <summary>
    /// The CEL type of <c>this</c> for a field, as the reference implementation derives it:
    /// generic collections for standard rules, element-typed ones for custom rules.
    /// </summary>
    private static CelType ThisType(ProtoTypeProvider provider, FieldDescriptor field, bool generic, bool forItems)
    {
        if (!forItems)
        {
            if (field.IsMap)
                return generic ? CelType.Map(CelType.Dyn, CelType.Dyn) : provider.FieldType(field);
            if (field.IsRepeated)
                return generic ? CelType.List(CelType.Dyn) : provider.FieldType(field);
        }
        return provider.ElementType(field);
    }

    private static CompiledRule Compile(CelEnvironment env, Rule rule, IReadOnlyList<FieldPathElement> rulePath)
    {
        CelProgram program;
        CheckResult check;
        try
        {
            program = env.Compile(rule.Expression, out check);
        }
        catch (CelCompilationException e)
        {
            throw new ValidationCompilationException($"failed to compile expression {rule.Id}: {e.Message}", e);
        }
        var type = check.ResultType;
        if (!(CelType.Bool.IsAssignableFrom(type) || CelType.String.IsAssignableFrom(type)))
        {
            throw new ValidationCompilationException(
                $"expression {rule.Id} outputs {type}, wanted either bool or string");
        }
        return new CompiledRule(program, rule, rulePath);
    }

    private static IEnumerable<Rule> ExpressionsToRules(IEnumerable<string> expressions) =>
        expressions.Select(e => new Rule { Id = e, Expression = e });

    // ---- messages ----

    private void BuildMessage(MessageDescriptor descriptor, MessagePlan plan)
    {
        var messageRules = descriptor.GetOptions()?.GetExtension(ValidateExtensions.Message);
        var provider = ProviderFor(descriptor.File);

        try
        {
            ProcessMessageExpressions(descriptor, messageRules, plan, provider);
            ProcessMessageOneofRules(descriptor, messageRules, plan);
        }
        catch (ValidationCompilationException e)
        {
            plan.Error = e;
            return;
        }

        foreach (var oneof in descriptor.Oneofs)
        {
            var oneofRules = oneof.GetOptions()?.GetExtension(ValidateExtensions.Oneof);
            if (oneofRules != null && oneofRules.Required)
                plan.AppendNested(new RequiredOneof(oneof));
        }

        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            var fieldRules = field.GetOptions()?.GetExtension(ValidateExtensions.Field);
            plan.AppendNested(BuildField(field, fieldRules, messageRules, provider));
        }
    }

    private void ProcessMessageExpressions(MessageDescriptor descriptor, MessageRules? messageRules, MessagePlan plan,
        ProtoTypeProvider provider)
    {
        if (messageRules == null)
            return;
        var rules = ExpressionsToRules(messageRules.CelExpression).Concat(messageRules.Cel).ToList();
        if (rules.Count == 0)
            return;
        var env = NewEnvironment(provider, CelType.Message(descriptor.FullName));
        var compiled = rules.Select(r => Compile(env, r, Array.Empty<FieldPathElement>())).ToList();
        plan.Append(new MessageCelRules(provider, compiled));
    }

    private static void ProcessMessageOneofRules(MessageDescriptor descriptor, MessageRules? messageRules, MessagePlan plan)
    {
        if (messageRules == null)
            return;
        foreach (var rule in messageRules.Oneof)
        {
            if (rule.Fields.Count == 0)
            {
                throw new ValidationCompilationException(
                    $"at least one field must be specified in oneof rule for the message {descriptor.FullName}");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var fields = new List<FieldDescriptor>(rule.Fields.Count);
            foreach (var name in rule.Fields)
            {
                if (!seen.Add(name))
                    throw new ValidationCompilationException($"duplicate {name} in oneof rule for the message {descriptor.FullName}");
                var field = descriptor.FindFieldByName(name);
                if (field == null)
                    throw new ValidationCompilationException($"field {name} not found in message {descriptor.FullName}");
                fields.Add(field);
            }
            plan.AppendNested(new MessageOneof(fields, rule.Required));
        }
    }

    private static bool IsPartOfMessageOneof(MessageRules? messageRules, FieldDescriptor field) =>
        messageRules != null && messageRules.Oneof.Any(o => o.Fields.Contains(field.Name));

    // ---- fields ----

    private FieldEvaluator BuildField(FieldDescriptor field, FieldRules? fieldRules, MessageRules? messageRules,
        ProtoTypeProvider provider)
    {
        if (fieldRules != null && !fieldRules.HasIgnore && IsPartOfMessageOneof(messageRules, field))
        {
            fieldRules = fieldRules.Clone();
            fieldRules.Ignore = Ignore.IfZeroValue;
        }
        var evaluator = new FieldEvaluator(field)
        {
            Required = fieldRules?.Required ?? false,
            Ignore = fieldRules?.Ignore ?? Ignore.Unspecified,
        };
        try
        {
            BuildValue(field, fieldRules, evaluator.Value, forItems: false, provider);
        }
        catch (ValidationCompilationException e)
        {
            evaluator.Error = e;
        }
        return evaluator;
    }

    private void BuildValue(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        if (rules?.Ignore == Ignore.Always)
            return;

        ProcessIgnoreEmpty(field, rules, value, forItems);
        ProcessFieldExpressions(field, rules, value, forItems, provider);
        ProcessEmbeddedMessage(field, value, forItems);
        ProcessWrapperRules(field, rules, value, forItems, provider);
        ProcessStandardRules(field, rules, value, forItems, provider);
        ProcessAnyRules(field, rules, value, forItems);
        ProcessMapRules(field, rules, value, provider);
        ProcessRepeatedRules(field, rules, value, forItems, provider);
    }

    private static void ProcessIgnoreEmpty(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems)
    {
        // Only a repeated item or a map key/value can be ignored for being empty here; a
        // field's own presence is decided by its FieldEvaluator.
        value.IgnoreEmpty = forItems && rules?.Ignore == Ignore.IfZeroValue;
        if (value.IgnoreEmpty)
        {
            var defaultMessage = IsMessageField(field) ? field.MessageType.Parser.ParseFrom(Array.Empty<byte>()) : null;
            value.IsZero = raw => IsZeroElement(raw, defaultMessage);
        }
    }

    private static bool IsMessageField(FieldDescriptor field) =>
        field.FieldType is FieldType.Message or FieldType.Group;

    private static bool IsZeroElement(object? raw, IMessage? defaultMessage) => raw switch
    {
        null => true,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        uint u => u == 0,
        ulong ul => ul == 0,
        float f => f == 0f,
        double d => d == 0d,
        string s => s.Length == 0,
        ByteString bs => bs.IsEmpty,
        System.Enum e => Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture) == 0,
        IMessage m => defaultMessage != null && m.Equals(defaultMessage),
        _ => false,
    };

    private void ProcessFieldExpressions(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        if (rules == null || (rules.CelExpression.Count == 0 && rules.Cel.Count == 0))
            return;
        var env = NewEnvironment(provider, ThisType(provider, field, generic: false, forItems));
        var compiled = new List<CompiledRule>();
        var expressions = ExpressionsToRules(rules.CelExpression).ToList();
        for (int i = 0; i < expressions.Count; i++)
        {
            var path = new[] { FieldPathElements.ForItem(CelExpressionElement, i) };
            compiled.Add(Compile(env, expressions[i], path));
        }
        for (int i = 0; i < rules.Cel.Count; i++)
        {
            var path = new[] { FieldPathElements.ForItem(CelElement, i) };
            compiled.Add(Compile(env, rules.Cel[i], path));
        }
        value.Append(new FieldCelRules(provider, field, forItems, compiled));
    }

    private void ProcessEmbeddedMessage(FieldDescriptor field, ValueRules value, bool forItems)
    {
        if (!IsMessageField(field) || field.IsMap || (field.IsRepeated && !forItems))
            return;
        if (ProtoValues.IsWrapper(field.MessageType))
            return; // a wrapper's value is the unwrapped primitive; there is nothing to descend into
        var plan = Build(field.MessageType);
        if (plan.Error != null)
        {
            throw new ValidationCompilationException(
                $"failed to compile embedded type {field.MessageType.FullName} for {field.FullName}: {plan.Error.Message}",
                plan.Error);
        }
        value.AppendNested(new EmbeddedMessage(plan));
    }

    private static FieldDescriptor? SetRulesField(FieldRules rules) =>
        rules.TypeCase == FieldRules.TypeOneofCase.None ? null : FieldRules.Descriptor.FindFieldByNumber((int)rules.TypeCase);

    private void ProcessWrapperRules(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        if (!IsMessageField(field) || field.IsMap || (field.IsRepeated && !forItems) || rules == null)
            return;
        var setRules = SetRulesField(rules);
        if (setRules == null)
            return;
        var isWrapper = ExpectedWrapperRules.TryGetValue(field.MessageType.FullName, out var expectedNumber);
        if (isWrapper && setRules.FieldNumber != expectedNumber)
        {
            throw new ValidationCompilationException(
                $"expected rule \"{FieldRules.Descriptor.FindFieldByNumber(expectedNumber).FullName}\", got \"{setRules.FullName}\" on field \"{field.FullName}\"");
        }
        if (!isWrapper)
            return;

        // Only the type rules apply to the inner value; the outer pipeline already handled the
        // rest (cel, cel_expression, ...), which would otherwise run twice.
        var innerRules = new FieldRules();
        setRules.Accessor.SetValue(innerRules, setRules.Accessor.GetValue(rules));
        var unwrapped = new ValueRules { Descriptor = value.Descriptor, NestedRulePrefix = value.NestedRulePrefix };
        BuildValue(field.MessageType.FindFieldByNumber(1), innerRules, unwrapped, forItems, provider);
        value.Rules.AddRange(unwrapped.Rules);
    }

    private void ProcessStandardRules(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        if (rules == null)
            return;
        // Wrapper fields were unwrapped above; a list of wrappers still needs its list rules.
        if (IsMessageField(field) && (!field.IsRepeated || forItems) && ExpectedWrapperRules.ContainsKey(field.MessageType.FullName))
            return;

        if (field.FieldType == FieldType.Enum && rules.TypeCase == FieldRules.TypeOneofCase.Enum && rules.Enum.DefinedOnly)
            value.Append(new DefinedEnum(field));

        AppendStandardRules(field, rules, value, forItems, provider);
    }

    private (int Number, bool Known) ExpectedRuleNumber(FieldDescriptor field, bool forItems)
    {
        if (field.IsMap)
            return (FieldRules.MapFieldNumber, true);
        if (field.IsRepeated && !forItems)
            return (FieldRules.RepeatedFieldNumber, true);
        if (IsMessageField(field))
            return ExpectedWellKnownRules.TryGetValue(field.MessageType.FullName, out var n) ? (n, true) : (0, false);
        return ExpectedScalarRules.TryGetValue(field.FieldType, out var s) ? (s, true) : (0, false);
    }

    private void AppendStandardRules(FieldDescriptor field, FieldRules rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        var setRules = SetRulesField(rules);
        if (setRules == null)
            return;
        var (expected, known) = ExpectedRuleNumber(field, forItems);
        if (known && setRules.FieldNumber != expected)
        {
            throw new ValidationCompilationException(
                $"expected rule \"{FieldRules.Descriptor.FindFieldByNumber(expected).FullName}\", got \"{setRules.FullName}\" on field \"{field.FullName}\"");
        }
        if (!known)
        {
            // The only expected rules for message fields are the well-known types'; any other
            // message with a type rule is a mismatch.
            if (IsMessageField(field))
            {
                throw new ValidationCompilationException(
                    $"mismatched message rules, \"{setRules.FullName}\" is not a valid rule for field \"{field.FullName}\"");
            }
            return;
        }

        var rulesMessage = (IMessage)setRules.Accessor.GetValue(rules);
        var rulesValue = new ProtoMessageValue(provider, rulesMessage);
        var compiled = new List<CompiledRule>();
        foreach (var ruleField in SetRuleFields(rulesMessage, provider))
        {
            var asts = StandardRuleAsts(setRules, ruleField);
            if (asts.Count == 0)
                continue;
            var raw = ruleField.Accessor.GetValue(rulesMessage);
            var ruleValue = ProtoValues.ValueToCel(provider, ruleField, raw, forItems: false);
            foreach (var ast in asts)
                compiled.Add(ast.WithRuleValues(rulesValue, ruleValue, raw, ruleField));
        }
        if (compiled.Count > 0)
            value.Append(new FieldCelRules(provider, field, forItems, compiled));
    }

    /// <summary>
    /// The populated fields of a rule message: declared fields in declaration order, then
    /// extensions (predefined rules) by field number.
    /// </summary>
    private static IEnumerable<FieldDescriptor> SetRuleFields(IMessage rulesMessage, ProtoTypeProvider provider)
    {
        var descriptor = rulesMessage.Descriptor;
        foreach (var f in descriptor.Fields.InDeclarationOrder())
        {
            if (ProtoValues.IsSet(rulesMessage, f))
                yield return f;
        }
        foreach (var ext in provider.ExtensionsOf(descriptor).OrderBy(e => e.FieldNumber))
        {
            if (ext.Extension != null && ProtoValues.IsSet(rulesMessage, ext))
                yield return ext;
        }
    }

    /// <summary>
    /// The compiled <c>(buf.validate.predefined).cel</c> expressions of one rule field, with
    /// <c>this</c>, <c>rules</c> and <c>rule</c> declared but unbound; compiled once per rule
    /// field and bound to each use's values.
    /// </summary>
    private IReadOnlyList<CompiledRule> StandardRuleAsts(FieldDescriptor setRules, FieldDescriptor ruleField)
    {
        if (_standardRules.TryGetValue(ruleField, out var cached))
            return cached;

        var predefined = ruleField.GetOptions()?.GetExtension(ValidateExtensions.Predefined);
        var rules = predefined?.Cel;
        IReadOnlyList<CompiledRule> result;
        if (rules == null || rules.Count == 0)
        {
            result = Array.Empty<CompiledRule>();
        }
        else
        {
            var provider = ProviderFor(ruleField.File);
            var thisType = ThisTypeOfRules(setRules);
            var env = NewEnvironment(provider, thisType);
            env.AddVariable("rules", CelType.Message(setRules.MessageType.FullName));
            env.AddVariable("rule", provider.FieldType(ruleField));
            var path = new[] { FieldPathElements.ForField(setRules), FieldPathElements.ForField(ruleField) };
            var list = new List<CompiledRule>(rules.Count);
            foreach (var rule in rules)
            {
                try
                {
                    list.Add(Compile(env, rule, path));
                }
                catch (ValidationCompilationException e)
                {
                    throw new ValidationCompilationException($"failed to compile standard rule \"{ruleField.FullName}\": {e.Message}", e);
                }
            }
            result = list;
        }
        _standardRules.AddOrUpdate(ruleField, result);
        return result;
    }

    /// <summary>The type of <c>this</c> for a rule message: fixed by the rule type, as the reference's generic conversion makes it.</summary>
    private static CelType ThisTypeOfRules(FieldDescriptor setRules)
    {
        switch (setRules.FieldNumber)
        {
            case FieldRules.MapFieldNumber: return CelType.Map(CelType.Dyn, CelType.Dyn);
            case FieldRules.RepeatedFieldNumber: return CelType.List(CelType.Dyn);
            case FieldRules.AnyFieldNumber: return CelType.Message("google.protobuf.Any");
            case FieldRules.DurationFieldNumber: return CelType.Message("google.protobuf.Duration");
            case FieldRules.TimestampFieldNumber: return CelType.Message("google.protobuf.Timestamp");
            case FieldRules.FieldMaskFieldNumber: return CelType.Message("google.protobuf.FieldMask");
            case FieldRules.EnumFieldNumber: return CelType.Int;
            default:
                var scalar = ExpectedScalarRules.First(kv => kv.Value == setRules.FieldNumber).Key;
                return ProtoTypeProvider.ScalarType(scalar);
        }
    }

    private static void ProcessAnyRules(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems)
    {
        if ((field.IsRepeated && !forItems) || !IsMessageField(field) || field.MessageType.FullName != "google.protobuf.Any")
            return;
        if (rules?.TypeCase != FieldRules.TypeOneofCase.Any)
            return;
        value.Append(new AnyTypeUrl(field, rules.Any));
    }

    private void ProcessMapRules(FieldDescriptor field, FieldRules? rules, ValueRules value, ProtoTypeProvider provider)
    {
        if (!field.IsMap)
            return;
        var mapRules = rules?.TypeCase == FieldRules.TypeOneofCase.Map ? rules.Map : null;
        var keys = new ValueRules { NestedRulePrefix = KeysPrefix };
        var values = new ValueRules { NestedRulePrefix = ValuesPrefix };
        try
        {
            BuildValue(field.MessageType.FindFieldByNumber(1), mapRules?.Keys, keys, forItems: true, provider);
        }
        catch (ValidationCompilationException e)
        {
            throw new ValidationCompilationException($"failed to compile key rules for map {field.FullName}: {e.Message}", e);
        }
        try
        {
            BuildValue(field.MessageType.FindFieldByNumber(2), mapRules?.Values, values, forItems: true, provider);
        }
        catch (ValidationCompilationException e)
        {
            throw new ValidationCompilationException($"failed to compile value rules for map {field.FullName}: {e.Message}", e);
        }
        value.Append(new MapPairs(field, FieldPathElements.ForField(field), keys, values));
    }

    private void ProcessRepeatedRules(FieldDescriptor field, FieldRules? rules, ValueRules value, bool forItems,
        ProtoTypeProvider provider)
    {
        if (!field.IsRepeated || field.IsMap || forItems)
            return;
        var itemRules = rules?.TypeCase == FieldRules.TypeOneofCase.Repeated ? rules.Repeated.Items : null;
        var items = new ValueRules { NestedRulePrefix = ItemsPrefix };
        try
        {
            BuildValue(field, itemRules, items, forItems: true, provider);
        }
        catch (ValidationCompilationException e)
        {
            throw new ValidationCompilationException($"failed to compile items rules for repeated {field.FullName}: {e.Message}", e);
        }
        value.Append(new ListItems(field, FieldPathElements.ForField(field), items));
    }
}
