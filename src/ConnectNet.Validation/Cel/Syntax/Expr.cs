using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ConnectNet.Validation.Cel.Syntax;

/// <summary>
/// Abstract syntax tree of a CEL expression. The shape mirrors <c>cel.expr.Expr</c> so that the
/// checker and the evaluator can be reasoned about against the specification directly. Every
/// node carries an <see cref="Id"/> that is unique within one parse; the <see cref="SourceInfo"/>
/// maps ids back to source positions.
/// </summary>
internal abstract class Expr
{
    protected Expr(long id)
    {
        Id = id;
    }

    public long Id { get; }

    public abstract ExprKind Kind { get; }

    /// <summary>Renders the tree as a compact S-expression, for tests and diagnostics.</summary>
    public string ToDebugString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    internal abstract void Write(StringBuilder sb);

    public override string ToString() => ToDebugString();
}

internal enum ExprKind
{
    Literal,
    Ident,
    Select,
    Call,
    List,
    Map,
    Struct,
    Comprehension,
}

/// <summary>Literal kinds carried by <see cref="LiteralExpr"/>.</summary>
internal enum LiteralKind
{
    Null,
    Bool,
    Int,
    Uint,
    Double,
    String,
    Bytes,
}

internal sealed class LiteralExpr : Expr
{
    private LiteralExpr(long id, LiteralKind literalKind, object? value) : base(id)
    {
        LiteralKind = literalKind;
        Value = value;
    }

    public override ExprKind Kind => ExprKind.Literal;

    public LiteralKind LiteralKind { get; }

    /// <summary>
    /// The literal value: <c>null</c>, <see cref="bool"/>, <see cref="long"/>, <see cref="ulong"/>,
    /// <see cref="double"/>, <see cref="string"/> or <c>byte[]</c>.
    /// </summary>
    public object? Value { get; }

    public static LiteralExpr Null(long id) => new(id, LiteralKind.Null, null);
    public static LiteralExpr Bool(long id, bool value) => new(id, LiteralKind.Bool, value);
    public static LiteralExpr Int(long id, long value) => new(id, LiteralKind.Int, value);
    public static LiteralExpr Uint(long id, ulong value) => new(id, LiteralKind.Uint, value);
    public static LiteralExpr Double(long id, double value) => new(id, LiteralKind.Double, value);
    public static LiteralExpr String(long id, string value) => new(id, LiteralKind.String, value);
    public static LiteralExpr Bytes(long id, byte[] value) => new(id, LiteralKind.Bytes, value);

    internal override void Write(StringBuilder sb)
    {
        switch (LiteralKind)
        {
            case LiteralKind.Null:
                sb.Append("null");
                break;
            case LiteralKind.Bool:
                sb.Append((bool)Value! ? "true" : "false");
                break;
            case LiteralKind.Int:
                sb.Append(((long)Value!).ToString(CultureInfo.InvariantCulture));
                break;
            case LiteralKind.Uint:
                sb.Append(((ulong)Value!).ToString(CultureInfo.InvariantCulture)).Append('u');
                break;
            case LiteralKind.Double:
                sb.Append(((double)Value!).ToString("R", CultureInfo.InvariantCulture));
                break;
            case LiteralKind.String:
                sb.Append('"').Append(EscapeForDebug((string)Value!)).Append('"');
                break;
            case LiteralKind.Bytes:
                sb.Append("b\"");
                foreach (var b in (byte[])Value!)
                {
                    if (b >= 0x20 && b < 0x7f && b != '"' && b != '\\')
                        sb.Append((char)b);
                    else
                        sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                sb.Append('"');
                break;
        }
    }

    internal static string EscapeForDebug(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

internal sealed class IdentExpr : Expr
{
    public IdentExpr(long id, string name) : base(id)
    {
        Name = name;
    }

    public override ExprKind Kind => ExprKind.Ident;

    /// <summary>The identifier. A leading <c>.</c> marks an absolute (root-scoped) name.</summary>
    public string Name { get; }

    internal override void Write(StringBuilder sb) => sb.Append(Name);
}

internal sealed class SelectExpr : Expr
{
    public SelectExpr(long id, Expr operand, string field, bool testOnly) : base(id)
    {
        Operand = operand;
        Field = field;
        TestOnly = testOnly;
    }

    public override ExprKind Kind => ExprKind.Select;

    public Expr Operand { get; }

    public string Field { get; }

    /// <summary>True for the <c>has(e.f)</c> macro: tests presence instead of selecting.</summary>
    public bool TestOnly { get; }

    internal override void Write(StringBuilder sb)
    {
        if (TestOnly) sb.Append("has(");
        Operand.Write(sb);
        sb.Append('.').Append(Field);
        if (TestOnly) sb.Append(')');
    }
}

internal sealed class CallExpr : Expr
{
    public CallExpr(long id, string function, Expr? target, IReadOnlyList<Expr> args) : base(id)
    {
        Function = function;
        Target = target;
        Args = args;
    }

    public override ExprKind Kind => ExprKind.Call;

    /// <summary>
    /// The function name. Operators use their canonical overload-independent names from
    /// <see cref="Operators"/>, e.g. <c>_+_</c> or <c>_[_]</c>.
    /// </summary>
    public string Function { get; }

    /// <summary>The receiver for a member call (<c>target.f(args)</c>), otherwise null.</summary>
    public Expr? Target { get; }

    public IReadOnlyList<Expr> Args { get; }

    internal override void Write(StringBuilder sb)
    {
        if (Target != null)
        {
            Target.Write(sb);
            sb.Append('.');
        }
        sb.Append(Function).Append('(');
        for (int i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Args[i].Write(sb);
        }
        sb.Append(')');
    }
}

internal sealed class ListExpr : Expr
{
    public ListExpr(long id, IReadOnlyList<Expr> elements) : base(id)
    {
        Elements = elements;
    }

    public override ExprKind Kind => ExprKind.List;

    public IReadOnlyList<Expr> Elements { get; }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('[');
        for (int i = 0; i < Elements.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Elements[i].Write(sb);
        }
        sb.Append(']');
    }
}

internal sealed class MapEntry
{
    public MapEntry(long id, Expr key, Expr value)
    {
        Id = id;
        Key = key;
        Value = value;
    }

    public long Id { get; }
    public Expr Key { get; }
    public Expr Value { get; }
}

internal sealed class MapExpr : Expr
{
    public MapExpr(long id, IReadOnlyList<MapEntry> entries) : base(id)
    {
        Entries = entries;
    }

    public override ExprKind Kind => ExprKind.Map;

    public IReadOnlyList<MapEntry> Entries { get; }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('{');
        for (int i = 0; i < Entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Entries[i].Key.Write(sb);
            sb.Append(": ");
            Entries[i].Value.Write(sb);
        }
        sb.Append('}');
    }
}

internal sealed class FieldEntry
{
    public FieldEntry(long id, string field, Expr value)
    {
        Id = id;
        Field = field;
        Value = value;
    }

    public long Id { get; }
    public string Field { get; }
    public Expr Value { get; }
}

/// <summary>Message construction, <c>pkg.Msg{field: value}</c>.</summary>
internal sealed class StructExpr : Expr
{
    public StructExpr(long id, string messageName, IReadOnlyList<FieldEntry> entries) : base(id)
    {
        MessageName = messageName;
        Entries = entries;
    }

    public override ExprKind Kind => ExprKind.Struct;

    /// <summary>The message name as written; a leading <c>.</c> marks an absolute name.</summary>
    public string MessageName { get; }

    public IReadOnlyList<FieldEntry> Entries { get; }

    internal override void Write(StringBuilder sb)
    {
        sb.Append(MessageName).Append('{');
        for (int i = 0; i < Entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Entries[i].Field).Append(": ");
            Entries[i].Value.Write(sb);
        }
        sb.Append('}');
    }
}

/// <summary>
/// The fold that macros expand into. Evaluates <see cref="AccuInit"/> into <see cref="AccuVar"/>,
/// then for each element of <see cref="IterRange"/> bound to <see cref="IterVar"/>, while
/// <see cref="LoopCondition"/> holds, assigns <see cref="LoopStep"/> to the accumulator; the value
/// is <see cref="Result"/>.
/// </summary>
internal sealed class ComprehensionExpr : Expr
{
    public ComprehensionExpr(long id, string iterVar, Expr iterRange, string accuVar, Expr accuInit,
        Expr loopCondition, Expr loopStep, Expr result) : base(id)
    {
        IterVar = iterVar;
        IterRange = iterRange;
        AccuVar = accuVar;
        AccuInit = accuInit;
        LoopCondition = loopCondition;
        LoopStep = loopStep;
        Result = result;
    }

    public override ExprKind Kind => ExprKind.Comprehension;

    public string IterVar { get; }
    public Expr IterRange { get; }
    public string AccuVar { get; }
    public Expr AccuInit { get; }
    public Expr LoopCondition { get; }
    public Expr LoopStep { get; }
    public Expr Result { get; }

    internal override void Write(StringBuilder sb)
    {
        sb.Append("__comprehension__(");
        sb.Append(IterVar).Append(", ");
        IterRange.Write(sb);
        sb.Append(", ").Append(AccuVar).Append(", ");
        AccuInit.Write(sb);
        sb.Append(", ");
        LoopCondition.Write(sb);
        sb.Append(", ");
        LoopStep.Write(sb);
        sb.Append(", ");
        Result.Write(sb);
        sb.Append(')');
    }
}
