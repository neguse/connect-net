using System;
using System.Collections.Generic;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Syntax;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>Resolves message types for construction; supplied by the Protobuf adaptation layer.</summary>
internal interface IMessageFactoryProvider
{
    IMessageFactory? FindMessageFactory(string typeName);
}

/// <summary>The compiled plan of one expression.</summary>
internal sealed class CelProgram
{
    private readonly Interpretable _root;
    private readonly int _slotCount;

    internal CelProgram(Interpretable root, int slotCount)
    {
        _root = root;
        _slotCount = slotCount;
    }

    /// <summary>Evaluates the plan. Errors are returned as <see cref="ErrorValue"/>; budget exhaustion and cancellation throw.</summary>
    public CelValue Evaluate(Activation activation, long budget = EvalContext.DefaultBudget,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var ctx = new EvalContext(activation, _slotCount, budget, cancellationToken);
        return _root.Eval(ctx);
    }

    /// <summary>Evaluates the plan against a budget shared with other evaluations.</summary>
    public CelValue Evaluate(Activation activation, EvalBudget budget) =>
        _root.Eval(new EvalContext(activation, _slotCount, budget));
}

/// <summary>
/// Turns a (checked or merely parsed) tree into an evaluation plan. Checked trees carry fully
/// qualified names and constants; parsed trees resolve names through the container at runtime.
/// </summary>
internal sealed class Planner
{
    private readonly Container _container;
    private readonly TypeProvider _provider;
    private readonly FunctionRegistry _functions;
    private readonly IMessageFactoryProvider? _messageFactories;
    private readonly IReadOnlyDictionary<long, Reference>? _references;
    private readonly List<Dictionary<string, int>> _scopes = new();
    private int _slotCount;

    private Planner(Container container, TypeProvider provider, FunctionRegistry functions,
        IMessageFactoryProvider? messageFactories, IReadOnlyDictionary<long, Reference>? references)
    {
        _container = container;
        _provider = provider;
        _functions = functions;
        _messageFactories = messageFactories;
        _references = references;
    }

    public static CelProgram Plan(CheckResult checked_, Container container, TypeProvider provider,
        FunctionRegistry functions, IMessageFactoryProvider? messageFactories = null)
    {
        if (checked_.Expr == null)
            throw new ArgumentException("cannot plan a failed check", nameof(checked_));
        var planner = new Planner(container, provider, functions, messageFactories, checked_.References);
        var root = planner.PlanExpr(checked_.Expr);
        return new CelProgram(root, planner._slotCount);
    }

    public static CelProgram Plan(ParseResult parsed, Container container, TypeProvider provider,
        FunctionRegistry functions, IMessageFactoryProvider? messageFactories = null)
    {
        if (parsed.Expr == null)
            throw new ArgumentException("cannot plan a failed parse", nameof(parsed));
        var planner = new Planner(container, provider, functions, messageFactories, null);
        var root = planner.PlanExpr(parsed.Expr);
        return new CelProgram(root, planner._slotCount);
    }

    private bool TryGetSlot(string name, out int slot)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out slot))
                return true;
        }
        slot = -1;
        return false;
    }

    private Interpretable PlanExpr(Expr e)
    {
        switch (e)
        {
            case LiteralExpr lit:
                return new ConstNode(e.Id, LiteralValue(lit));
            case IdentExpr ident:
                return PlanIdent(ident);
            case SelectExpr sel:
                return PlanSelect(sel);
            case CallExpr call:
                return PlanCall(call);
            case ListExpr list:
            {
                var elements = new Interpretable[list.Elements.Count];
                for (int i = 0; i < elements.Length; i++)
                    elements[i] = PlanExpr(list.Elements[i]);
                return new ListNode(e.Id, elements);
            }
            case MapExpr map:
            {
                var keys = new Interpretable[map.Entries.Count];
                var values = new Interpretable[map.Entries.Count];
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = PlanExpr(map.Entries[i].Key);
                    values[i] = PlanExpr(map.Entries[i].Value);
                }
                return new MapNode(e.Id, keys, values);
            }
            case StructExpr st:
                return PlanStruct(st);
            case ComprehensionExpr comp:
                return PlanComprehension(comp);
            default:
                throw new InvalidOperationException("unexpected expression kind " + e.Kind);
        }
    }

    private static CelValue LiteralValue(LiteralExpr lit) => lit.LiteralKind switch
    {
        LiteralKind.Null => NullValue.Instance,
        LiteralKind.Bool => BoolValue.Of((bool)lit.Value!),
        LiteralKind.Int => IntValue.Of((long)lit.Value!),
        LiteralKind.Uint => UintValue.Of((ulong)lit.Value!),
        LiteralKind.Double => DoubleValue.Of((double)lit.Value!),
        LiteralKind.String => StringValue.Of((string)lit.Value!),
        _ => new BytesValue((byte[])lit.Value!),
    };

    private Interpretable PlanIdent(IdentExpr ident)
    {
        if (TryGetSlot(ident.Name, out var slot))
            return new SlotNode(ident.Id, slot);

        if (_references != null && _references.TryGetValue(ident.Id, out var reference) && reference.Name != null)
        {
            if (reference.ConstantValue is long constant)
                return new ConstNode(ident.Id, IntValue.Of(constant));
            if (reference.ConstantValue is CelValue constantValue)
                return new ConstNode(ident.Id, constantValue);
            return new GlobalVarNode(ident.Id, new[] { reference.Name.TrimStart('.') }, _provider);
        }
        return new GlobalVarNode(ident.Id, _container.ResolveCandidateNames(ident.Name), _provider);
    }

    private Interpretable PlanSelect(SelectExpr sel)
    {
        if (_references != null && _references.TryGetValue(sel.Id, out var reference) && reference.Name != null)
        {
            if (reference.ConstantValue is long constant)
                return new ConstNode(sel.Id, IntValue.Of(constant));
            if (reference.ConstantValue is CelValue constantValue)
                return new ConstNode(sel.Id, constantValue);
            return new GlobalVarNode(sel.Id, new[] { reference.Name.TrimStart('.') }, _provider);
        }

        if (_references == null && !sel.TestOnly)
        {
            // Unchecked: a select chain over a global identifier may name a variable.
            var fields = new List<string>();
            Expr cur = sel;
            while (cur is SelectExpr s && !s.TestOnly)
            {
                fields.Add(s.Field);
                cur = s.Operand;
            }
            if (cur is IdentExpr root && !TryGetSlot(root.Name, out _))
            {
                fields.Reverse();
                return new QualifiedPathNode(sel.Id, root.Name, fields.ToArray(), _container, _provider);
            }
        }

        var operand = PlanExpr(sel.Operand);
        return new SelectNode(sel.Id, operand, sel.Field, sel.TestOnly);
    }

    private Interpretable PlanCall(CallExpr call)
    {
        switch (call.Function)
        {
            case Operators.LogicalAnd when call.Target == null:
                return new LogicalNode(call.Id, PlanArgs(call), isAnd: true);
            case Operators.LogicalOr when call.Target == null:
                return new LogicalNode(call.Id, PlanArgs(call), isAnd: false);
            case Operators.Conditional when call.Target == null && call.Args.Count == 3:
                return new ConditionalNode(call.Id, PlanExpr(call.Args[0]), PlanExpr(call.Args[1]), PlanExpr(call.Args[2]));
            case Operators.NotStrictlyFalse when call.Target == null && call.Args.Count == 1:
                return new NotStrictlyFalseNode(call.Id, PlanExpr(call.Args[0]));
        }

        var args = new List<Interpretable>(call.Args.Count + 1);
        ICelFunction? function = null;
        string name = call.Function;

        if (call.Target != null)
        {
            // Unchecked: a.b.f(x) may be the namespaced function a.b.f.
            if (_references == null)
            {
                var qualified = ToQualifiedName(call.Target);
                if (qualified != null && TryLookupFunction(qualified + "." + call.Function, out function, out name))
                {
                    foreach (var a in call.Args) args.Add(PlanExpr(a));
                    return new CallNode(call.Id, function, args.ToArray());
                }
            }
            args.Add(PlanExpr(call.Target));
        }
        foreach (var a in call.Args)
            args.Add(PlanExpr(a));

        if (function == null && !TryLookupFunction(call.Function, out function, out name))
            return new UnboundCallNode(call.Id, call.Function, args.ToArray());
        return new CallNode(call.Id, function, args.ToArray());
    }

    private Interpretable[] PlanArgs(CallExpr call)
    {
        var args = new Interpretable[call.Args.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = PlanExpr(call.Args[i]);
        return args;
    }

    private bool TryLookupFunction(string name, out ICelFunction function, out string resolvedName)
    {
        if (_references != null)
        {
            resolvedName = name;
            return _functions.TryGet(name, out function);
        }
        foreach (var candidate in _container.ResolveCandidateNames(name))
        {
            if (_functions.TryGet(candidate, out function))
            {
                resolvedName = candidate;
                return true;
            }
        }
        function = null!;
        resolvedName = name;
        return false;
    }

    private static string? ToQualifiedName(Expr e)
    {
        switch (e)
        {
            case IdentExpr ident:
                return ident.Name;
            case SelectExpr sel when !sel.TestOnly:
            {
                var prefix = ToQualifiedName(sel.Operand);
                return prefix == null ? null : prefix + "." + sel.Field;
            }
            default:
                return null;
        }
    }

    private Interpretable PlanStruct(StructExpr st)
    {
        var fields = new string[st.Entries.Count];
        var values = new Interpretable[st.Entries.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = st.Entries[i].Field;
            values[i] = PlanExpr(st.Entries[i].Value);
        }

        IMessageFactory? factory = null;
        if (_messageFactories != null)
        {
            if (_references != null)
            {
                factory = _messageFactories.FindMessageFactory(st.MessageName.TrimStart('.'));
            }
            else
            {
                foreach (var candidate in _container.ResolveCandidateNames(st.MessageName))
                {
                    factory = _messageFactories.FindMessageFactory(candidate);
                    if (factory != null) break;
                }
            }
        }
        if (factory == null)
            return new ConstNode(st.Id, new ErrorValue("unknown type: '" + st.MessageName + "'", st.Id));
        return new StructNode(st.Id, factory, fields, values);
    }

    private Interpretable PlanComprehension(ComprehensionExpr comp)
    {
        var range = PlanExpr(comp.IterRange);
        var init = PlanExpr(comp.AccuInit);

        int accuSlot = _slotCount++;
        int iterSlot = _slotCount++;
        _scopes.Add(new Dictionary<string, int>(StringComparer.Ordinal) { [comp.AccuVar] = accuSlot });
        _scopes.Add(new Dictionary<string, int>(StringComparer.Ordinal) { [comp.IterVar] = iterSlot });
        var condition = PlanExpr(comp.LoopCondition);
        var step = PlanExpr(comp.LoopStep);
        _scopes.RemoveAt(_scopes.Count - 1);
        var result = PlanExpr(comp.Result);
        _scopes.RemoveAt(_scopes.Count - 1);
        return new ComprehensionNode(comp.Id, iterSlot, accuSlot, range, init, condition, step, result);
    }
}
