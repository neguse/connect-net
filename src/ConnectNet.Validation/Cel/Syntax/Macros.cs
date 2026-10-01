using System;
using System.Collections.Generic;

namespace ConnectNet.Validation.Cel.Syntax;

/// <summary>
/// Helper handed to macro expanders so they can mint expression nodes with fresh ids whose
/// positions point at the macro call.
/// </summary>
internal sealed class MacroExprHelper
{
    private readonly Parser _parser;
    private readonly int _offset;

    internal MacroExprHelper(Parser parser, int offset)
    {
        _parser = parser;
        _offset = offset;
    }

    /// <summary>The accumulator variable name of comprehensions produced by the standard macros.</summary>
    public const string AccumulatorName = "__result__";

    public IdentExpr AccuIdent() => new(_parser.NextId(_offset), AccumulatorName);

    public IdentExpr Ident(string name) => new(_parser.NextId(_offset), name);

    public LiteralExpr Bool(bool value) => LiteralExpr.Bool(_parser.NextId(_offset), value);

    public LiteralExpr Int(long value) => LiteralExpr.Int(_parser.NextId(_offset), value);

    public ListExpr List(params Expr[] elements) => new(_parser.NextId(_offset), elements);

    public CallExpr Call(string function, params Expr[] args) => new(_parser.NextId(_offset), function, null, args);

    public SelectExpr PresenceTest(Expr operand, string field) => new(_parser.NextId(_offset), operand, field, testOnly: true);

    public ComprehensionExpr Comprehension(Expr iterRange, string iterVar, string accuVar, Expr accuInit,
        Expr condition, Expr step, Expr result) =>
        new(_parser.NextId(_offset), iterVar, iterRange, accuVar, accuInit, condition, step, result);

    public MacroException Error(Expr at, string message) => new(message, at.Id);
}

/// <summary>Raised by a macro expander to report an invalid macro call.</summary>
internal sealed class MacroException : Exception
{
    public MacroException(string message, long exprId) : base(message)
    {
        ExprId = exprId;
    }

    public long ExprId { get; }
}

internal delegate Expr MacroExpander(MacroExprHelper helper, Expr? target, IReadOnlyList<Expr> args);

/// <summary>A macro: a call shape (name, arity, receiver style) and its expansion.</summary>
internal sealed class Macro
{
    public Macro(string function, int argCount, bool receiverStyle, MacroExpander expander)
    {
        Function = function;
        ArgCount = argCount;
        ReceiverStyle = receiverStyle;
        Expander = expander;
    }

    public string Function { get; }
    public int ArgCount { get; }
    public bool ReceiverStyle { get; }
    public MacroExpander Expander { get; }

    public string Key => MakeKey(Function, ArgCount, ReceiverStyle);

    public static string MakeKey(string function, int argCount, bool receiverStyle) =>
        function + ":" + argCount + ":" + (receiverStyle ? "r" : "g");
}

/// <summary>The macros of the CEL specification: has, all, exists, exists_one, map and filter.</summary>
internal static class StandardMacros
{
    public static readonly Macro Has = new("has", 1, false, ExpandHas);
    public static readonly Macro All = new("all", 2, true, (h, t, a) => ExpandQuantifier(Quantifier.All, h, t!, a));
    public static readonly Macro Exists = new("exists", 2, true, (h, t, a) => ExpandQuantifier(Quantifier.Exists, h, t!, a));
    public static readonly Macro ExistsOne = new("exists_one", 2, true, (h, t, a) => ExpandQuantifier(Quantifier.ExistsOne, h, t!, a));
    public static readonly Macro Map = new("map", 2, true, ExpandMap);
    public static readonly Macro MapFilter = new("map", 3, true, ExpandMap);
    public static readonly Macro Filter = new("filter", 2, true, ExpandFilter);

    public static readonly IReadOnlyList<Macro> All_ = new[] { Has, All, Exists, ExistsOne, Map, MapFilter, Filter };

    private enum Quantifier { All, Exists, ExistsOne }

    private static Expr ExpandHas(MacroExprHelper helper, Expr? target, IReadOnlyList<Expr> args)
    {
        if (args[0] is SelectExpr select && !select.TestOnly)
            return helper.PresenceTest(select.Operand, select.Field);
        throw helper.Error(args[0], "invalid argument to has() macro");
    }

    private static string ExtractIterVar(MacroExprHelper helper, Expr arg, string errorMessage)
    {
        if (arg is not IdentExpr ident)
            throw helper.Error(arg, errorMessage);
        if (ident.Name == MacroExprHelper.AccumulatorName)
            throw helper.Error(arg, "iteration variable overwrites accumulator variable");
        return ident.Name;
    }

    private static Expr ExpandQuantifier(Quantifier kind, MacroExprHelper helper, Expr target, IReadOnlyList<Expr> args)
    {
        var v = ExtractIterVar(helper, args[0], "argument must be a simple name");
        Expr init, condition, step, result;
        switch (kind)
        {
            case Quantifier.All:
                init = helper.Bool(true);
                condition = helper.Call(Operators.NotStrictlyFalse, helper.AccuIdent());
                step = helper.Call(Operators.LogicalAnd, helper.AccuIdent(), args[1]);
                result = helper.AccuIdent();
                break;
            case Quantifier.Exists:
                init = helper.Bool(false);
                condition = helper.Call(Operators.NotStrictlyFalse, helper.Call(Operators.LogicalNot, helper.AccuIdent()));
                step = helper.Call(Operators.LogicalOr, helper.AccuIdent(), args[1]);
                result = helper.AccuIdent();
                break;
            default:
                init = helper.Int(0);
                condition = helper.Bool(true);
                step = helper.Call(Operators.Conditional, args[1],
                    helper.Call(Operators.Add, helper.AccuIdent(), helper.Int(1)), helper.AccuIdent());
                result = helper.Call(Operators.Equals, helper.AccuIdent(), helper.Int(1));
                break;
        }
        return helper.Comprehension(target, v, MacroExprHelper.AccumulatorName, init, condition, step, result);
    }

    private static Expr ExpandMap(MacroExprHelper helper, Expr? target, IReadOnlyList<Expr> args)
    {
        var v = ExtractIterVar(helper, args[0], "argument is not an identifier");
        Expr? filter = null;
        Expr fn;
        if (args.Count == 3)
        {
            filter = args[1];
            fn = args[2];
        }
        else
        {
            fn = args[1];
        }
        var init = helper.List();
        var condition = helper.Bool(true);
        Expr step = helper.Call(Operators.Add, helper.AccuIdent(), helper.List(fn));
        if (filter != null)
            step = helper.Call(Operators.Conditional, filter, step, helper.AccuIdent());
        return helper.Comprehension(target!, v, MacroExprHelper.AccumulatorName, init, condition, step, helper.AccuIdent());
    }

    private static Expr ExpandFilter(MacroExprHelper helper, Expr? target, IReadOnlyList<Expr> args)
    {
        var v = ExtractIterVar(helper, args[0], "argument is not an identifier");
        var init = helper.List();
        var condition = helper.Bool(true);
        Expr step = helper.Call(Operators.Add, helper.AccuIdent(), helper.List(args[0]));
        step = helper.Call(Operators.Conditional, args[1], step, helper.AccuIdent());
        return helper.Comprehension(target!, v, MacroExprHelper.AccumulatorName, init, condition, step, helper.AccuIdent());
    }
}
