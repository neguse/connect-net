using System.Collections.Generic;

namespace ConnectNet.Validation.Cel.Syntax;

/// <summary>Canonical function names of CEL operators, as used in <see cref="CallExpr.Function"/>.</summary>
internal static class Operators
{
    public const string Conditional = "_?_:_";
    public const string LogicalAnd = "_&&_";
    public const string LogicalOr = "_||_";
    public const string LogicalNot = "!_";
    public const string EqualsOp = "_==_";
    public const string NotEquals = "_!=_";
    public const string Less = "_<_";
    public const string LessEquals = "_<=_";
    public const string Greater = "_>_";
    public const string GreaterEquals = "_>=_";
    public const string Add = "_+_";
    public const string Subtract = "_-_";
    public const string Multiply = "_*_";
    public const string Divide = "_/_";
    public const string Modulo = "_%_";
    public const string Negate = "-_";
    public const string Index = "_[_]";
    public const string In = "@in";

    /// <summary>Internal: <c>@not_strictly_false(x)</c> is true unless x is exactly false.</summary>
    public const string NotStrictlyFalse = "@not_strictly_false";

    private static readonly Dictionary<string, string> DisplayNames = new()
    {
        [Conditional] = "?:",
        [LogicalAnd] = "&&",
        [LogicalOr] = "||",
        [LogicalNot] = "!",
        [EqualsOp] = "==",
        [NotEquals] = "!=",
        [Less] = "<",
        [LessEquals] = "<=",
        [Greater] = ">",
        [GreaterEquals] = ">=",
        [Add] = "+",
        [Subtract] = "-",
        [Multiply] = "*",
        [Divide] = "/",
        [Modulo] = "%",
        [Negate] = "-",
        [Index] = "[]",
        [In] = "in",
    };

    private static readonly Dictionary<string, string> ReverseNames = new()
    {
        ["<"] = Less,
        ["<="] = LessEquals,
        [">"] = Greater,
        [">="] = GreaterEquals,
        ["=="] = EqualsOp,
        ["!="] = NotEquals,
        ["in"] = In,
        ["+"] = Add,
        ["-"] = Subtract,
        ["*"] = Multiply,
        ["/"] = Divide,
        ["%"] = Modulo,
    };

    public static bool TryGetDisplayName(string function, out string display) =>
        DisplayNames.TryGetValue(function, out display!);

    public static string FromBinaryToken(string token) => ReverseNames[token];
}
