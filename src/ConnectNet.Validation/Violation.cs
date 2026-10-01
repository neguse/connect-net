using System;
using ConnectNet.Validation.Evaluation;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation;

/// <summary>
/// One failed rule. <see cref="Field"/> and <see cref="Rule"/> carry the structured paths of
/// the protovalidate contract; <see cref="FieldPath"/> is their textual rendering.
/// </summary>
public class Violation
{
    /// <summary>
    /// Hard cap on the length of the violation message stored on this object. Rule messages
    /// interpolate attacker-controlled values; without this cap a hostile request could blow
    /// up both heap usage and the reflected error JSON size.
    /// </summary>
    public const int MaxMessageLength = 512;

    private readonly Buf.Validate.Violation _proto;
    private string? _fieldPath;

    /// <summary>The textual field path, e.g. <c>items[0].name</c> or <c>entries["key"]</c>; empty for message-level rules.</summary>
    public string FieldPath => _fieldPath ??= FieldPathElements.Format(_proto.Field);

    /// <summary>The rule id, e.g. <c>string.min_len</c>, <c>required</c>, or a custom rule's id.</summary>
    public string ConstraintId => _proto.RuleId;

    public string Message => _proto.Message;

    /// <summary>The value of the field that failed, as the reflection accessor returns it; null when there is none.</summary>
    public object? Value { get; }

    /// <summary>The value of the rule that failed (a standard rule's own setting); null for custom rules.</summary>
    public object? RuleValue { get; }

    /// <summary>The descriptor of the field that failed, when the violation belongs to a field.</summary>
    public FieldDescriptor? FieldDescriptor { get; }

    /// <summary>The descriptor of the rule field that failed, for standard and predefined rules.</summary>
    public FieldDescriptor? RuleDescriptor { get; }

    /// <summary>The structured path of the field that failed; null for message-level rules.</summary>
    public Buf.Validate.FieldPath? Field => _proto.Field;

    /// <summary>The structured path of the rule that failed within <c>buf.validate.FieldRules</c>; null when there is none.</summary>
    public Buf.Validate.FieldPath? Rule => _proto.Rule;

    /// <summary>
    /// True when the violation applies to a map key rather than the map value at
    /// <see cref="FieldPath"/> (protovalidate's <c>Violation.for_key</c>).
    /// </summary>
    public bool ForKey
    {
        get => _proto.ForKey;
        internal set => _proto.ForKey = value;
    }

    public Violation(string fieldPath, string constraintId, string message, object? value = null)
    {
        _proto = new Buf.Validate.Violation
        {
            RuleId = constraintId,
            Message = Truncate(message, MaxMessageLength),
        };
        if (!string.IsNullOrEmpty(fieldPath))
            _proto.Field = ParseFieldPath(fieldPath);
        Value = value;
    }

    internal Violation(Buf.Validate.Violation proto, object? value, object? ruleValue,
        FieldDescriptor? field, FieldDescriptor? ruleField)
    {
        _proto = proto;
        Value = value;
        RuleValue = ruleValue;
        FieldDescriptor = field;
        RuleDescriptor = ruleField;
    }

    /// <summary>The violation as the <c>buf.validate.Violation</c> carried in error details.</summary>
    public Buf.Validate.Violation ToProto() => _proto.Clone();

    public override string ToString() =>
        FieldPath.Length == 0 ? $"{Message} [{ConstraintId}]" : $"{FieldPath}: {Message} [{ConstraintId}]";

    /// <summary>
    /// Truncates a value snippet for inclusion in a violation message, so a single oversize
    /// input cannot dominate the resulting <see cref="Message"/>.
    /// </summary>
    public static string Truncate(string? s, int max)
    {
        if (s == null) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    /// <summary>
    /// Parses a textual path such as <c>inner.name</c>, <c>items[0].name</c> or
    /// <c>entries["key"]</c> into path elements carrying only names and subscripts, for
    /// violations constructed from a string path.
    /// </summary>
    internal static Buf.Validate.FieldPath ParseFieldPath(string path)
    {
        var fieldPath = new Buf.Validate.FieldPath();
        int i = 0;
        int n = path.Length;
        while (i < n)
        {
            int start = i;
            bool inQuotes = false;
            while (i < n && (inQuotes || (path[i] != '.' && path[i] != '[')))
            {
                if (path[i] == '"') inQuotes = !inQuotes;
                i++;
            }

            var element = new Buf.Validate.FieldPathElement
            {
                FieldName = path.Substring(start, i - start),
            };

            if (i < n && path[i] == '[')
            {
                int close = FindSubscriptEnd(path, i + 1);
                ApplySubscript(element, path.Substring(i + 1, close - i - 1));
                i = close < n ? close + 1 : n;
            }

            fieldPath.Elements.Add(element);

            if (i < n && path[i] == '.')
                i++;
        }
        return fieldPath;
    }

    private static int FindSubscriptEnd(string path, int start)
    {
        bool inQuotes = false;
        for (int i = start; i < path.Length; i++)
        {
            if (path[i] == '"') inQuotes = !inQuotes;
            else if (path[i] == ']' && !inQuotes) return i;
        }
        return path.Length; // malformed; consume the rest
    }

    private static void ApplySubscript(Buf.Validate.FieldPathElement element, string subscript)
    {
        if (subscript.Length >= 2 && subscript[0] == '"' && subscript[subscript.Length - 1] == '"')
        {
            element.StringKey = subscript.Substring(1, subscript.Length - 2);
        }
        else if (subscript == "true" || subscript == "false")
        {
            element.BoolKey = subscript == "true";
        }
        else if (ulong.TryParse(subscript, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            // Non-negative integers are ambiguous between repeated indices and integer map
            // keys in the textual form; repeated indices are by far the common case.
            element.Index = index;
        }
        else if (long.TryParse(subscript, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var intKey))
        {
            element.IntKey = intKey;
        }
        else
        {
            element.StringKey = subscript;
        }
    }
}
