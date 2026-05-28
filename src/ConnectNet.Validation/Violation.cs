namespace ConnectNet.Validation;

public class Violation
{
    /// <summary>
    /// Hard cap on the length of the violation message stored on this object. Rule
    /// evaluators interpolate attacker-controlled values into messages; without this cap
    /// a hostile request could blow up both heap usage and the reflected error JSON size.
    /// </summary>
    public const int MaxMessageLength = 512;

    public string FieldPath { get; }
    public string ConstraintId { get; }
    public string Message { get; }
    public object? Value { get; }

    public Violation(string fieldPath, string constraintId, string message, object? value = null)
    {
        FieldPath = fieldPath;
        ConstraintId = constraintId;
        Message = Truncate(message, MaxMessageLength);
        Value = value;
    }

    /// <summary>
    /// Truncates a value snippet for inclusion in a violation message. Use in rule evaluators
    /// before interpolating untrusted strings so a single oversize input cannot dominate the
    /// resulting <see cref="Message"/>.
    /// </summary>
    public static string Truncate(string? s, int max)
    {
        if (s == null) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }
}
