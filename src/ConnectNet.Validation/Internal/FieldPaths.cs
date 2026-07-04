using System.Globalization;
using System.Text;

namespace ConnectNet.Validation.Internal;

/// <summary>
/// Helpers for rendering protovalidate-style field paths, e.g. <c>items[0]</c>,
/// <c>entries["key"]</c>, <c>counts[42]</c>, <c>flags[true]</c>.
/// </summary>
internal static class FieldPaths
{
    private const int MaxKeyDisplayLength = 64;

    /// <summary>Renders a map key as a subscript suffix, e.g. <c>["key"]</c> / <c>[42]</c> / <c>[true]</c>.</summary>
    public static string MapKeySubscript(object? key)
    {
        return key switch
        {
            string s => "[\"" + Escape(s) + "\"]",
            bool b => b ? "[true]" : "[false]",
            int i => "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
            long l => "[" + l.ToString(CultureInfo.InvariantCulture) + "]",
            uint u => "[" + u.ToString(CultureInfo.InvariantCulture) + "]",
            ulong ul => "[" + ul.ToString(CultureInfo.InvariantCulture) + "]",
            _ => "[" + Escape(key?.ToString() ?? "") + "]",
        };
    }

    /// <summary>
    /// Escapes a map key for inclusion in a field path. Attacker-controlled map keys flow
    /// into FieldPath; control characters here would corrupt log lines (CR/LF injection) or
    /// terminal output (ANSI escape sequences). Long keys are truncated for display.
    /// </summary>
    public static string Escape(string s)
    {
        if (s.Length == 0) return s;
        StringBuilder? sb = null;
        var limit = s.Length > MaxKeyDisplayLength ? MaxKeyDisplayLength : s.Length;
        for (int i = 0; i < limit; i++)
        {
            var c = s[i];
            if (c < 0x20 || c == 0x7f || c == '"' || c == '\\' || c == '[' || c == ']')
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 8);
                sb.Append('\\');
                sb.Append('u');
                sb.Append(((int)c).ToString("x4"));
            }
            else
            {
                sb?.Append(c);
            }
        }
        if (sb == null)
        {
            return s.Length > limit ? s.Substring(0, limit) + "..." : s;
        }
        if (s.Length > limit) sb.Append("...");
        return sb.ToString();
    }
}
