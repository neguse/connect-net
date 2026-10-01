using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ConnectNet.Validation.Cel.Syntax;

/// <summary>
/// The text of one expression as a sequence of Unicode code points, with line offsets for
/// diagnostics. CEL positions count code points, not UTF-16 units.
/// </summary>
internal sealed class Source
{
    private readonly int[] _lineStarts;

    public Source(string text, string description = "<input>")
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Description = description;
        CodePoints = ToCodePoints(text);

        var lines = new List<int> { 0 };
        for (int i = 0; i < CodePoints.Length; i++)
        {
            if (CodePoints[i] == '\n')
                lines.Add(i + 1);
        }
        _lineStarts = lines.ToArray();
    }

    public string Text { get; }

    public string Description { get; }

    /// <summary>
    /// The code points of <see cref="Text"/>. An unpaired surrogate is kept as its own
    /// (invalid) code point so the lexer can reject it.
    /// </summary>
    public int[] CodePoints { get; }

    public int Length => CodePoints.Length;

    /// <summary>Converts a code point offset to a 1-based line and column.</summary>
    public (int Line, int Column) Locate(int offset)
    {
        if (offset < 0) offset = 0;
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo + 1, offset - _lineStarts[lo] + 1);
    }

    /// <summary>Returns the text of the 1-based line, without its terminator.</summary>
    public string LineText(int line)
    {
        if (line < 1 || line > _lineStarts.Length) return "";
        int start = _lineStarts[line - 1];
        int end = line < _lineStarts.Length ? _lineStarts[line] - 1 : CodePoints.Length;
        return Slice(start, end);
    }

    public string Slice(int start, int end)
    {
        if (start < 0) start = 0;
        if (end > CodePoints.Length) end = CodePoints.Length;
        if (end <= start) return "";
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
            AppendCodePoint(sb, CodePoints[i]);
        return sb.ToString();
    }

    internal static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp < 0x10000)
            sb.Append((char)cp);
        else
            sb.Append(char.ConvertFromUtf32(cp));
    }

    private static int[] ToCodePoints(string s)
    {
        var result = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                result.Add(char.ConvertToUtf32(c, s[i + 1]));
                i++;
            }
            else
            {
                result.Add(c);
            }
        }
        return result.ToArray();
    }
}

/// <summary>One diagnostic produced while parsing or checking an expression.</summary>
internal sealed class CelError
{
    public CelError(string message, int offset, long exprId = 0)
    {
        Message = message;
        Offset = offset;
        ExprId = exprId;
    }

    public string Message { get; }

    /// <summary>Code point offset into the source, or -1 when the error has no position.</summary>
    public int Offset { get; }

    public long ExprId { get; }

    /// <summary>Renders the error in the <c>ERROR: &lt;input&gt;:line:col: message</c> form.</summary>
    public string Format(Source source)
    {
        if (Offset < 0)
            return "ERROR: " + source.Description + ": " + Message;

        var (line, col) = source.Locate(Offset);
        var sb = new StringBuilder();
        sb.Append("ERROR: ").Append(source.Description).Append(':')
            .Append(line.ToString(CultureInfo.InvariantCulture)).Append(':')
            .Append(col.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(Message);
        var text = source.LineText(line);
        if (text.Length > 0)
        {
            sb.Append("\n | ").Append(text);
            sb.Append("\n | ");
            // Column is counted in code points; align the caret by code points of the line.
            int cpCol = 0;
            for (int i = 0; i < text.Length && cpCol < col - 1; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                cpCol++;
                sb.Append('.');
            }
            sb.Append('^');
        }
        return sb.ToString();
    }

    public override string ToString() => Message;
}

/// <summary>Bounded error sink shared by the parser and the checker.</summary>
internal sealed class CelErrors
{
    public const int DefaultLimit = 100;

    private readonly List<CelError> _errors = new();

    public CelErrors(Source source, int limit = DefaultLimit)
    {
        Source = source;
        Limit = limit;
    }

    public Source Source { get; }

    public int Limit { get; }

    public IReadOnlyList<CelError> Errors => _errors;

    public int Count => _errors.Count;

    public bool HasErrors => _errors.Count > 0;

    public void Report(string message, int offset, long exprId = 0)
    {
        if (_errors.Count < Limit)
            _errors.Add(new CelError(message, offset, exprId));
    }

    public string FormatAll()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _errors.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(_errors[i].Format(Source));
        }
        return sb.ToString();
    }
}

/// <summary>Positions of every expression id of one parse, plus the macro calls that were expanded.</summary>
internal sealed class SourceInfo
{
    private readonly Dictionary<long, int> _positions = new();
    private readonly Dictionary<long, Expr> _macroCalls = new();

    public SourceInfo(Source source)
    {
        Source = source;
    }

    public Source Source { get; }

    public IReadOnlyDictionary<long, int> Positions => _positions;

    /// <summary>The original call expression of every macro that was expanded, keyed by the id of the expansion.</summary>
    public IReadOnlyDictionary<long, Expr> MacroCalls => _macroCalls;

    public void SetPosition(long id, int offset) => _positions[id] = offset;

    public int GetPosition(long id) => _positions.TryGetValue(id, out var offset) ? offset : -1;

    public void AddMacroCall(long id, Expr call) => _macroCalls[id] = call;

    public (int Line, int Column) Locate(long id)
    {
        var offset = GetPosition(id);
        return offset < 0 ? (0, 0) : Source.Locate(offset);
    }
}
