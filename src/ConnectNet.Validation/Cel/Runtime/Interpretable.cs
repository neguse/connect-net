using System;
using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>A CEL function at runtime: receives every argument evaluated (the receiver first for member calls).</summary>
internal interface ICelFunction
{
    string Name { get; }

    CelValue Invoke(EvalContext ctx, CelValue[] args);
}

/// <summary>Functions available to a program, by name.</summary>
internal sealed class FunctionRegistry
{
    private readonly Dictionary<string, ICelFunction> _functions = new(StringComparer.Ordinal);

    public FunctionRegistry Add(ICelFunction function)
    {
        _functions[function.Name] = function;
        return this;
    }

    public bool TryGet(string name, out ICelFunction function) => _functions.TryGetValue(name, out function!);
}

/// <summary>One node of an evaluation plan.</summary>
internal abstract class Interpretable
{
    protected Interpretable(long id)
    {
        Id = id;
    }

    public long Id { get; }

    public abstract CelValue Eval(EvalContext ctx);
}

internal sealed class ConstNode : Interpretable
{
    private readonly CelValue _value;

    public ConstNode(long id, CelValue value) : base(id)
    {
        _value = value;
    }

    public override CelValue Eval(EvalContext ctx) => _value;
}

internal sealed class SlotNode : Interpretable
{
    private readonly int _slot;

    public SlotNode(long id, int slot) : base(id)
    {
        _slot = slot;
    }

    public override CelValue Eval(EvalContext ctx) => ctx.Slots[_slot];
}

/// <summary>
/// A global name resolved at evaluation time: the activation first, then type names and enum
/// values from the provider, for each candidate name in order.
/// </summary>
internal sealed class GlobalVarNode : Interpretable
{
    private readonly IReadOnlyList<string> _candidates;
    private readonly TypeProvider _provider;

    public GlobalVarNode(long id, IReadOnlyList<string> candidates, TypeProvider provider) : base(id)
    {
        _candidates = candidates;
        _provider = provider;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        foreach (var name in _candidates)
        {
            if (TryResolve(ctx, _provider, name, out var value))
                return value;
        }
        return new ErrorValue("no such attribute(s): " + string.Join(", ", _candidates), Id);
    }

    internal static bool TryResolve(EvalContext ctx, TypeProvider provider, string name, out CelValue value)
    {
        if (ctx.Activation.TryResolve(name, out value))
            return true;
        var type = provider.FindType(name);
        if (type != null)
        {
            value = TypeValue.Of(type);
            return true;
        }
        if (provider.TryFindEnumConstant(name, out _, out var enumConstant))
        {
            value = enumConstant is CelValue cv ? cv : IntValue.Of((long)enumConstant);
            return true;
        }
        value = null!;
        return false;
    }
}

/// <summary>
/// An unchecked <c>a.b.c</c>: the longest prefix that names a variable (or type, or enum value)
/// in the container wins, and the remaining names select fields from it.
/// </summary>
internal sealed class QualifiedPathNode : Interpretable
{
    private readonly string _root;
    private readonly string[] _fields;
    private readonly Container _container;
    private readonly TypeProvider _provider;

    public QualifiedPathNode(long id, string root, string[] fields, Container container, TypeProvider provider) : base(id)
    {
        _root = root;
        _fields = fields;
        _container = container;
        _provider = provider;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1 + _fields.Length);
        for (int k = _fields.Length; k >= 0; k--)
        {
            var qualified = k == 0 ? _root : _root + "." + string.Join(".", _fields, 0, k);
            foreach (var candidate in _container.ResolveCandidateNames(qualified))
            {
                if (!GlobalVarNode.TryResolve(ctx, _provider, candidate, out var value))
                    continue;
                for (int i = k; i < _fields.Length; i++)
                {
                    value = SelectNode.SelectField(value, _fields[i], Id);
                    if (value.IsError) return value;
                }
                return value;
            }
        }
        return new ErrorValue("no such attribute(s): " + string.Join(", ", _container.ResolveCandidateNames(_root)), Id);
    }
}

internal sealed class SelectNode : Interpretable
{
    private readonly Interpretable _operand;
    private readonly string _field;
    private readonly bool _testOnly;

    public SelectNode(long id, Interpretable operand, string field, bool testOnly) : base(id)
    {
        _operand = operand;
        _field = field;
        _testOnly = testOnly;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        var operand = _operand.Eval(ctx);
        if (operand.IsError) return operand;
        return _testOnly ? TestField(operand, _field, Id) : SelectField(operand, _field, Id);
    }

    internal static CelValue SelectField(CelValue operand, string field, long id)
    {
        switch (operand)
        {
            case MapValue map:
                return map.TryGet(StringValue.Of(field), out var value)
                    ? value
                    : new ErrorValue("no such key: " + field, id);
            case MessageValue message:
                return message.GetField(field);
            case ErrorValue:
                return operand;
            default:
                return new ErrorValue("no such overload: " + operand.TypeName + "." + field, id);
        }
    }

    internal static CelValue TestField(CelValue operand, string field, long id)
    {
        switch (operand)
        {
            case MapValue map:
                return BoolValue.Of(map.ContainsKey(StringValue.Of(field)));
            case MessageValue message:
                return message.HasField(field);
            case ErrorValue:
                return operand;
            default:
                return new ErrorValue("no such overload: has(" + operand.TypeName + "." + field + ")", id);
        }
    }
}

internal sealed class CallNode : Interpretable
{
    private readonly ICelFunction _function;
    private readonly Interpretable[] _args;

    public CallNode(long id, ICelFunction function, Interpretable[] args) : base(id)
    {
        _function = function;
        _args = args;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        var values = new CelValue[_args.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var v = _args[i].Eval(ctx);
            if (v.IsError) return v;
            values[i] = v;
        }
        var result = _function.Invoke(ctx, values);
        return result is ErrorValue e ? e.WithExprId(Id) : result;
    }
}

/// <summary>A call to a function that was not declared: always an error, after evaluating the arguments.</summary>
internal sealed class UnboundCallNode : Interpretable
{
    private readonly string _name;
    private readonly Interpretable[] _args;

    public UnboundCallNode(long id, string name, Interpretable[] args) : base(id)
    {
        _name = name;
        _args = args;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        foreach (var arg in _args)
        {
            var v = arg.Eval(ctx);
            if (v.IsError) return v;
        }
        return new ErrorValue("unbound function: " + _name, Id);
    }
}

internal sealed class LogicalNode : Interpretable
{
    private readonly Interpretable[] _terms;
    private readonly bool _isAnd;

    public LogicalNode(long id, Interpretable[] terms, bool isAnd) : base(id)
    {
        _terms = terms;
        _isAnd = isAnd;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        // `false` wins for &&, `true` wins for ||, whatever else the other terms produce; then an
        // error from any term; then a non-boolean term is a missing overload.
        ErrorValue? error = null;
        foreach (var term in _terms)
        {
            var v = term.Eval(ctx);
            if (v is BoolValue b)
            {
                if (b.Value != _isAnd)
                    return b;
                continue;
            }
            if (error == null)
                error = v is ErrorValue e ? e : ErrorValue.NoSuchOverload(_isAnd ? "_&&_" : "_||_", v).WithExprId(Id);
        }
        return (CelValue?)error ?? BoolValue.Of(_isAnd);
    }
}

internal sealed class NotStrictlyFalseNode : Interpretable
{
    private readonly Interpretable _arg;

    public NotStrictlyFalseNode(long id, Interpretable arg) : base(id)
    {
        _arg = arg;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        var v = _arg.Eval(ctx);
        return v is BoolValue b ? b : BoolValue.True;
    }
}

internal sealed class ConditionalNode : Interpretable
{
    private readonly Interpretable _condition;
    private readonly Interpretable _whenTrue;
    private readonly Interpretable _whenFalse;

    public ConditionalNode(long id, Interpretable condition, Interpretable whenTrue, Interpretable whenFalse) : base(id)
    {
        _condition = condition;
        _whenTrue = whenTrue;
        _whenFalse = whenFalse;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        var c = _condition.Eval(ctx);
        if (c is BoolValue b)
            return b.Value ? _whenTrue.Eval(ctx) : _whenFalse.Eval(ctx);
        if (c.IsError)
            return c;
        return ErrorValue.NoSuchOverload("_?_:_", c).WithExprId(Id);
    }
}

internal sealed class ListNode : Interpretable
{
    private readonly Interpretable[] _elements;

    public ListNode(long id, Interpretable[] elements) : base(id)
    {
        _elements = elements;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1 + _elements.Length);
        if (_elements.Length == 0)
            return ListValue.Empty;
        var values = new CelValue[_elements.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var v = _elements[i].Eval(ctx);
            if (v.IsError) return v;
            values[i] = v;
        }
        return new ListValue(values);
    }
}

internal sealed class MapNode : Interpretable
{
    private readonly Interpretable[] _keys;
    private readonly Interpretable[] _values;

    public MapNode(long id, Interpretable[] keys, Interpretable[] values) : base(id)
    {
        _keys = keys;
        _values = values;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1 + _keys.Length);
        if (_keys.Length == 0)
            return MapValue.Empty;
        var entries = new KeyValuePair<CelValue, CelValue>[_keys.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var k = _keys[i].Eval(ctx);
            if (k.IsError) return k;
            var v = _values[i].Eval(ctx);
            if (v.IsError) return v;
            entries[i] = new KeyValuePair<CelValue, CelValue>(k, v);
        }
        var map = MapValue.Create(entries);
        return map is ErrorValue e ? e.WithExprId(Id) : map;
    }
}

/// <summary>Builds a message; the factory is supplied by the Protobuf adaptation layer.</summary>
internal sealed class StructNode : Interpretable
{
    private readonly IMessageFactory _factory;
    private readonly string[] _fields;
    private readonly Interpretable[] _values;

    public StructNode(long id, IMessageFactory factory, string[] fields, Interpretable[] values) : base(id)
    {
        _factory = factory;
        _fields = fields;
        _values = values;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1 + _fields.Length);
        var values = new CelValue[_values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var v = _values[i].Eval(ctx);
            if (v.IsError) return v;
            values[i] = v;
        }
        var result = _factory.Create(_fields, values);
        return result is ErrorValue e ? e.WithExprId(Id) : result;
    }
}

/// <summary>Creates one message type from field names and CEL values; errors for bad fields or values.</summary>
internal interface IMessageFactory
{
    CelValue Create(string[] fields, CelValue[] values);
}

internal sealed class ComprehensionNode : Interpretable
{
    private readonly int _iterSlot;
    private readonly int _accuSlot;
    private readonly Interpretable _range;
    private readonly Interpretable _init;
    private readonly Interpretable _condition;
    private readonly Interpretable _step;
    private readonly Interpretable _result;

    public ComprehensionNode(long id, int iterSlot, int accuSlot, Interpretable range, Interpretable init,
        Interpretable condition, Interpretable step, Interpretable result) : base(id)
    {
        _iterSlot = iterSlot;
        _accuSlot = accuSlot;
        _range = range;
        _init = init;
        _condition = condition;
        _step = step;
        _result = result;
    }

    public override CelValue Eval(EvalContext ctx)
    {
        ctx.Consume(1);
        var range = _range.Eval(ctx);
        if (range.IsError) return range;
        IReadOnlyList<CelValue> items;
        switch (range)
        {
            case ListValue list:
                items = list.Elements;
                break;
            case MapValue map:
            {
                var keys = new CelValue[map.Count];
                for (int i = 0; i < keys.Length; i++) keys[i] = map.Entries[i].Key;
                items = keys;
                break;
            }
            default:
                return new ErrorValue("no such overload: comprehension over " + range.TypeName, Id);
        }

        var accu = _init.Eval(ctx);
        var slots = ctx.Slots;
        slots[_accuSlot] = accu;
        for (int i = 0; i < items.Count; i++)
        {
            ctx.Consume(1);
            slots[_iterSlot] = items[i];
            var cond = _condition.Eval(ctx);
            if (cond is BoolValue b && !b.Value)
                break;
            slots[_accuSlot] = _step.Eval(ctx);
        }
        return _result.Eval(ctx);
    }
}
